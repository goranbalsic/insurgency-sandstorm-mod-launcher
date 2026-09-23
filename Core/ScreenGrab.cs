using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SandstormModLauncher.Core
{
    /// <summary>A horizontal strip of the screen (the bottom of the game window) in 32-bit BGRA.</summary>
    public sealed class Frame
    {
        public int Width, Height;               // strip size in pixels
        public int ClientWidth, ClientHeight;   // size of the whole game picture
        public byte[] Bgra;
        public byte[] Lum;
        public DateTime TakenUtc;

        public byte L(int x, int y) => Lum[y * Width + x];

        public void SavePng(string path)
        {
            try
            {
                var src = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgr32, null, Bgra, Width * 4);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(src));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var fs = File.Create(path)) enc.Save(fs);
            }
            catch (Exception ex) { AppLog.Warn("Could not save " + Path.GetFileName(path) + ": " + ex.Message); }
        }
    }

    /// <summary>Copies pixels from the screen. Read-only: it never touches the game process.</summary>
    public static class ScreenGrab
    {
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public uint[] bmiColors;
        }

        private const int SRCCOPY = 0x00CC0020;

        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr bmp, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFO bi, uint usage);

        /// <summary>The bottom part of a window's client area (fraction of its height), or null when it cannot be read.</summary>
        public static Frame BottomOfWindow(IntPtr hwnd, double fraction = 0.16, int minRows = 64, int maxRows = 280)
        {
            if (hwnd == IntPtr.Zero) return null;
            IntPtr old = IntPtr.Zero;
            try { old = SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch { }
            try
            {
                if (!GetClientRect(hwnd, out RECT rc)) return null;
                int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
                if (w < 320 || h < 240) return null;
                var pt = new POINT();
                if (!ClientToScreen(hwnd, ref pt)) return null;
                int rows = Math.Max(Math.Min(minRows, h), Math.Min(Math.Min(maxRows, h), (int)(h * fraction)));
                var f = Capture(pt.X, pt.Y + h - rows, w, rows);
                if (f != null) { f.ClientWidth = w; f.ClientHeight = h; }
                return f;
            }
            catch (Exception ex) { AppLog.Warn("Screen capture failed: " + ex.Message); return null; }
            finally
            {
                if (old != IntPtr.Zero) { try { SetThreadDpiAwarenessContext(old); } catch { } }
            }
        }

        private static Frame Capture(int x, int y, int w, int h)
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return null;
            IntPtr mem = CreateCompatibleDC(screen);
            IntPtr bmp = CreateCompatibleBitmap(screen, w, h);
            try
            {
                IntPtr prev = SelectObject(mem, bmp);
                bool ok = BitBlt(mem, 0, 0, w, h, screen, x, y, SRCCOPY);
                SelectObject(mem, prev); // GetDIBits needs the bitmap deselected
                if (!ok) return null;
                var bi = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 },
                    bmiColors = new uint[4]
                };
                var data = new byte[w * h * 4];
                if (GetDIBits(mem, bmp, 0, (uint)h, data, ref bi, 0) != h) return null;
                var lum = new byte[w * h];
                for (int i = 0, p = 0; i < lum.Length; i++, p += 4)
                    lum[i] = (byte)((data[p] * 19 + data[p + 1] * 183 + data[p + 2] * 54) >> 8);
                return new Frame { Width = w, Height = h, Bgra = data, Lum = lum, TakenUtc = DateTime.UtcNow };
            }
            finally
            {
                DeleteObject(bmp);
                DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }
    }
}

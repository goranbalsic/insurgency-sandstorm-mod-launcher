using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SandstormModLauncher.Core;
using SandstormModLauncher.Game;

namespace SandstormModLauncher.Services
{
    /// <summary>
    /// Offline checks for the console detector (no game needed):
    ///   --probe-test &lt;out.txt&gt; &lt;screenshot.png...&gt;  paints console lines and look-alikes onto real screenshots
    ///   --probe &lt;out.txt&gt; &lt;before.png&gt; &lt;after.png&gt;   analyses two saved console pictures (logs\console)
    /// </summary>
    public static class ProbeTest
    {
        public static void Run(string outFile, IList<string> pngs)
        {
            var sb = new StringBuilder();
            int pass = 0, fail = 0;
            void Check(string name, bool ok, string detail)
            {
                if (ok) pass++; else fail++;
                sb.AppendLine((ok ? "PASS " : "FAIL ") + name + "  |  " + detail);
            }
            foreach (var png in pngs)
            {
                var full = Load(png, out int w, out int h);
                if (full == null) { sb.AppendLine("cannot read " + png); fail++; continue; }
                sb.AppendLine("== " + Path.GetFileName(png) + " " + w + "x" + h);
                int rows = Math.Max(Math.Min(64, h), Math.Min(Math.Min(280, h), (int)(h * 0.16)));
                Frame Strip(byte[] px) => Make(px, w, h, rows);
                var before = Strip(full);
                int lineH = (int)Math.Round(h * 0.0185) + 6; // small font height + padding at this resolution

                // Real console: opaque black bar with a grey top line and the prompt.
                var opaque = Strip(Bar(full, w, h, lineH, 0, true));
                var bar = ConsoleProbe.DetectOpened(before, opaque, out string why);
                Check("opaque console line is seen", bar != null, why);

                var see = Strip(Bar(full, w, h, lineH, 0.5, false));
                Check("see-through console line is seen", ConsoleProbe.DetectOpened(before, see, out why) != null, why);

                Check("unchanged picture is not a console", ConsoleProbe.DetectOpened(before, before, out why) == null, why);
                Check("camera turn is not a console", ConsoleProbe.DetectOpened(before, Strip(Shift(full, w, h, 14)), out why) == null, why);
                Check("fade to dark is not a console", ConsoleProbe.DetectOpened(before, Strip(Scale(full, 0.55)), out why) == null, why);
                Check("notification popup is not a console", ConsoleProbe.DetectOpened(before, Strip(Box(full, w, h, w - 360, h - 110, 340, 96, 60)), out why) == null, why);
                Check("dark box above the bottom is not a console", ConsoleProbe.DetectOpened(before, Strip(Box(full, w, h, 0, h - lineH * 4, w, lineH, 0)), out why) == null, why);
                var blackBottom = Box(full, w, h, 0, h - lineH - 20, w, lineH + 20, 0);
                Check("already black bottom stays unconfirmed", ConsoleProbe.DetectOpened(Strip(blackBottom), Strip(Bar(blackBottom, w, h, lineH, 0, false)), out why) == null, why);
                var night = Scale(full, 0.25);
                Check("console line on a night map is seen", ConsoleProbe.DetectOpened(Strip(night), Strip(Bar(night, w, h, lineH, 0, false)), out why) != null, why);
                Check("night map, unchanged, is not a console", ConsoleProbe.DetectOpened(Strip(night), Strip(night), out why) == null, why);
                Check("night map, camera turn, is not a console", ConsoleProbe.DetectOpened(Strip(night), Strip(Shift(night, w, h, 14)), out why) == null, why);

                if (bar != null)
                {
                    var typedPx = Text(Bar(full, w, h, lineH, 0, true), w, h, lineH, 0.55);
                    var typed = Strip(typedPx);
                    var check = ConsoleProbe.TextIn(opaque, typed, bar, out int px);
                    Check("typed text is seen", check == TextCheck.Appeared, check + " " + px + " px");
                    Check("empty line has no text", ConsoleProbe.TextIn(opaque, opaque, bar, out px) == TextCheck.None, px + " px");
                    Check("bar still there with text", ConsoleProbe.BarStillThere(opaque, typed, bar), "");
                    Check("bar gone when closed", !ConsoleProbe.BarStillThere(opaque, before, bar), "");
                    double keep = ConsoleProbe.TextRemaining(opaque, typed, typed, bar);
                    Check("text still showing", keep > 0.9, keep.ToString("0.00"));
                    keep = ConsoleProbe.TextRemaining(opaque, typed, before, bar);
                    Check("text gone after Enter", keep < 0.5, keep.ToString("0.00"));
                    var longText = Strip(Text(Bar(full, w, h, lineH, 0, true), w, h, lineH, 1.0));
                    check = ConsoleProbe.TextIn(opaque, longText, bar, out px);
                    Check("long command across the whole line is seen", check == TextCheck.Appeared, check + " " + px + " px");
                }
            }
            sb.AppendLine($"{pass} passed, {fail} failed");
            File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
        }

        public static void Analyse(string outFile, string beforePng, string afterPng)
        {
            var b = LoadStrip(beforePng);
            var a = LoadStrip(afterPng);
            var sb = new StringBuilder();
            if (a == null || b == null) sb.AppendLine("cannot read the pictures");
            else
            {
                var bar = ConsoleProbe.DetectOpened(b, a, out string why);
                sb.AppendLine("opened: " + (bar != null) + "  " + why);
                sb.AppendLine("steady: " + ConsoleProbe.Steady(b, a) + "  changed " + ConsoleProbe.Changed(b, a, 0, a.Height - 1).ToString("P1"));
                for (int y = Math.Max(0, a.Height - ConsoleProbe.BandRows(a)); y < a.Height; y++)
                    sb.AppendLine($"row {y}: changed {ConsoleProbe.Changed(b, a, y, y, 0.3, 0.97):P0}");
            }
            File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
        }

        // ------------------------------------------------------------------ picture helpers

        private static byte[] Load(string path, out int w, out int h)
        {
            w = h = 0;
            try
            {
                var dec = BitmapDecoder.Create(new Uri(Path.GetFullPath(path)), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var conv = new FormatConvertedBitmap(dec.Frames[0], PixelFormats.Bgr32, null, 0);
                w = conv.PixelWidth; h = conv.PixelHeight;
                var px = new byte[w * h * 4];
                conv.CopyPixels(px, w * 4, 0);
                return px;
            }
            catch { return null; }
        }

        private static Frame LoadStrip(string path)
        {
            var px = Load(path, out int w, out int h);
            if (px == null) return null;
            var f = Make(px, w, h, h);
            f.ClientHeight = (int)Math.Round(h / 0.16);
            return f;
        }

        private static Frame Make(byte[] full, int w, int h, int rows)
        {
            var data = new byte[w * rows * 4];
            Buffer.BlockCopy(full, (h - rows) * w * 4, data, 0, data.Length);
            var lum = new byte[w * rows];
            for (int i = 0, p = 0; i < lum.Length; i++, p += 4) lum[i] = (byte)((data[p] * 19 + data[p + 1] * 183 + data[p + 2] * 54) >> 8);
            return new Frame { Width = w, Height = rows, ClientWidth = w, ClientHeight = h, Bgra = data, Lum = lum, TakenUtc = DateTime.UtcNow };
        }

        private static void Set(byte[] px, int w, int x, int y, byte v)
        {
            int p = (y * w + x) * 4;
            px[p] = px[p + 1] = px[p + 2] = v;
        }

        /// <summary>The console line: black (alpha 1) or darkened (alpha &lt; 1) bar at the bottom, optional grey top border and prompt.</summary>
        private static byte[] Bar(byte[] src, int w, int h, int lineH, double keep, bool decorate)
        {
            var px = (byte[])src.Clone();
            for (int y = h - lineH; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int p = (y * w + x) * 4;
                    for (int c = 0; c < 3; c++) px[p + c] = (byte)(px[p + c] * keep);
                }
            if (decorate)
            {
                for (int x = 0; x < w; x++) { Set(px, w, x, h - lineH, 140); Set(px, w, x, h - lineH + 1, 140); }
                for (int y = h - lineH + 6; y < h - 5; y++) for (int x = 8; x < 12; x++) Set(px, w, x, y, 230);   // ">"
                for (int x = 22; x < 30; x++) Set(px, w, x, h - 6, 230);                                           // cursor
            }
            return px;
        }

        /// <summary>Glyph-like light marks along the line, covering the given share of the width.</summary>
        private static byte[] Text(byte[] src, int w, int h, int lineH, double share)
        {
            var px = (byte[])src.Clone();
            var rnd = new Random(7);
            int end = (int)(w * share) - 10;
            for (int x = 34; x < end; x += 7)
            {
                if (rnd.Next(6) == 0) continue; // spaces
                int top = h - lineH + 5, bottom = h - 5;
                for (int y = top; y < bottom; y++)
                    if (rnd.Next(3) == 0) { Set(px, w, x, y, 230); Set(px, w, x + 1, y, 230); }
            }
            return px;
        }

        private static byte[] Shift(byte[] src, int w, int h, int dx)
        {
            var px = new byte[src.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int sx = Math.Min(w - 1, Math.Max(0, x + dx));
                    Buffer.BlockCopy(src, (y * w + sx) * 4, px, (y * w + x) * 4, 4);
                }
            return px;
        }

        private static byte[] Scale(byte[] src, double f)
        {
            var px = (byte[])src.Clone();
            for (int i = 0; i < px.Length; i++) if (i % 4 != 3) px[i] = (byte)(px[i] * f);
            return px;
        }

        private static byte[] Box(byte[] src, int w, int h, int x0, int y0, int bw, int bh, byte v)
        {
            var px = (byte[])src.Clone();
            for (int y = Math.Max(0, y0); y < Math.Min(h, y0 + bh); y++)
                for (int x = Math.Max(0, x0); x < Math.Min(w, x0 + bw); x++) Set(px, w, x, y, v);
            return px;
        }
    }
}

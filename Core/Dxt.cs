using System;

namespace SandstormModLauncher.Core
{
    /// <summary>Decodes DXT1/DXT5 (BC1/BC3) and BGRA8 texture data into BGRA32 pixels.</summary>
    public static class Dxt
    {
        public static byte[] Decode(string format, int width, int height, byte[] data)
        {
            switch (format)
            {
                case "PF_DXT1": return DecodeBlocks(width, height, data, 8, false);
                case "PF_DXT5": return DecodeBlocks(width, height, data, 16, true);
                case "PF_B8G8R8A8":
                    return data.Length >= width * height * 4 ? data : null;
                default: return null;
            }
        }

        private static byte[] DecodeBlocks(int w, int h, byte[] src, int blockBytes, bool hasAlpha)
        {
            int bw = (w + 3) / 4, bh = (h + 3) / 4;
            if (src.Length < bw * bh * blockBytes) return null;
            var dst = new byte[w * h * 4];
            var colors = new byte[16];
            var alphas = new byte[8];
            int p = 0;
            for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                ulong alphaBits = 0;
                if (hasAlpha)
                {
                    alphas[0] = src[p]; alphas[1] = src[p + 1];
                    for (int i = 0; i < 6; i++) alphaBits |= (ulong)src[p + 2 + i] << (8 * i);
                    if (alphas[0] > alphas[1])
                        for (int i = 1; i <= 6; i++) alphas[1 + i] = (byte)(((7 - i) * alphas[0] + i * alphas[1]) / 7);
                    else
                    {
                        for (int i = 1; i <= 4; i++) alphas[1 + i] = (byte)(((5 - i) * alphas[0] + i * alphas[1]) / 5);
                        alphas[6] = 0; alphas[7] = 255;
                    }
                    p += 8;
                }
                ushort c0 = (ushort)(src[p] | src[p + 1] << 8), c1 = (ushort)(src[p + 2] | src[p + 3] << 8);
                uint bits = (uint)(src[p + 4] | src[p + 5] << 8 | src[p + 6] << 16 | src[p + 7] << 24);
                p += 8;
                Rgb565(c0, colors, 0); Rgb565(c1, colors, 4);
                bool fourColor = c0 > c1 || hasAlpha;
                for (int k = 0; k < 3; k++)
                {
                    if (fourColor)
                    {
                        colors[8 + k] = (byte)((2 * colors[k] + colors[4 + k]) / 3);
                        colors[12 + k] = (byte)((colors[k] + 2 * colors[4 + k]) / 3);
                    }
                    else
                    {
                        colors[8 + k] = (byte)((colors[k] + colors[4 + k]) / 2);
                        colors[12 + k] = 0;
                    }
                }
                colors[3] = colors[7] = colors[11] = 255;
                colors[15] = (byte)(fourColor ? 255 : 0);

                for (int py = 0; py < 4; py++)
                for (int px = 0; px < 4; px++)
                {
                    int x = bx * 4 + px, y = by * 4 + py;
                    if (x >= w || y >= h) continue;
                    int idx = (int)((bits >> (2 * (py * 4 + px))) & 3);
                    int o = (y * w + x) * 4;
                    dst[o] = colors[idx * 4 + 2];     // B
                    dst[o + 1] = colors[idx * 4 + 1]; // G
                    dst[o + 2] = colors[idx * 4];     // R
                    dst[o + 3] = hasAlpha ? alphas[(int)((alphaBits >> (3 * (py * 4 + px))) & 7)] : colors[idx * 4 + 3];
                }
            }
            return dst;
        }

        private static void Rgb565(ushort c, byte[] outp, int o)
        {
            int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
            outp[o] = (byte)((r << 3) | (r >> 2));
            outp[o + 1] = (byte)((g << 2) | (g >> 4));
            outp[o + 2] = (byte)((b << 3) | (b >> 2));
        }
    }
}

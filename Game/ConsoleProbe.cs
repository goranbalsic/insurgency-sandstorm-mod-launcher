using System;
using SandstormModLauncher.Core;

namespace SandstormModLauncher.Game
{
    /// <summary>Rows of the captured strip taken up by the one-line console (inclusive).</summary>
    public sealed class ConsoleBar
    {
        public int Top, Bottom;
        public override string ToString() => Top + "-" + Bottom;
    }

    public enum TextCheck { None, Appeared, Gone }

    /// <summary>
    /// Tells from screen pictures whether the game's one-line console is open. The launcher only
    /// sends editing keys after the console line was seen on screen, so keys can never reach the
    /// game's menus (where Backspace, Delete and Enter are bound to menu actions).
    /// </summary>
    public static class ConsoleProbe
    {
        /// <summary>Height of the band at the bottom of the picture where the console line is drawn.</summary>
        public static int BandRows(Frame f) => Math.Max(20, Math.Min(f.Height - 8, (int)Math.Round(f.ClientHeight * 0.06)));

        private static void Columns(Frame f, double from, double to, int samples, out int x0, out int x1, out int step)
        {
            x0 = (int)(f.Width * from);
            x1 = Math.Max(x0 + 1, (int)(f.Width * to));
            step = Math.Max(1, (x1 - x0) / samples);
        }

        private static bool Same(Frame a, Frame b) => a != null && b != null && a.Width == b.Width && a.Height == b.Height;

        /// <summary>Share of sampled pixels whose brightness moved by more than the threshold.</summary>
        public static double Changed(Frame a, Frame b, int y0, int y1, double from = 0, double to = 1, int threshold = 14)
        {
            if (!Same(a, b)) return 1;
            y0 = Math.Max(0, y0); y1 = Math.Min(a.Height - 1, y1);
            if (y1 < y0) return 0;
            Columns(a, from, to, 320, out int x0, out int x1, out int step);
            int n = 0, c = 0;
            for (int y = y0; y <= y1; y += 2)
                for (int x = x0; x < x1; x += step)
                {
                    n++;
                    if (Math.Abs(a.L(x, y) - b.L(x, y)) > threshold) c++;
                }
            return n == 0 ? 0 : (double)c / n;
        }

        /// <summary>True for an empty picture (all one colour), e.g. while fullscreen is switching back in.</summary>
        public static bool Blank(Frame f)
        {
            if (f == null) return true;
            Columns(f, 0, 1, 400, out int x0, out int x1, out int step);
            long n = 0; double sum = 0, sq = 0;
            for (int y = 0; y < f.Height; y += 4)
                for (int x = x0; x < x1; x += step)
                {
                    int v = f.L(x, y);
                    n++; sum += v; sq += v * v;
                }
            if (n == 0) return true;
            double mean = sum / n;
            return Math.Sqrt(Math.Max(0, sq / n - mean * mean)) < 2.5;
        }

        /// <summary>True when two pictures of the same place barely differ.</summary>
        public static bool Steady(Frame a, Frame b) => Same(a, b) && Changed(a, b, 0, a.Height - 1) < 0.08;

        /// <summary>True when anything moved on screen between the two pictures (the game is drawing frames).</summary>
        public static bool Alive(Frame a, Frame b)
        {
            if (!Same(a, b)) return true;
            int bandTop = a.Height - BandRows(a);
            Columns(a, 0, 1, 640, out int x0, out int x1, out int step);
            int moved = 0;
            for (int y = 0; y < bandTop - 2; y++)
                for (int x = x0; x < x1; x += step)
                    if (Math.Abs(a.L(x, y) - b.L(x, y)) > 6 && ++moved >= 12) return true;
            return false;
        }

        /// <summary>
        /// Compares the picture from before the console key with one taken after it. The console
        /// line is a dark bar across the whole width at the very bottom; everything above it stays
        /// as it was. Returns the bar rows, or null with the reason when it is not clearly there.
        /// </summary>
        public static ConsoleBar DetectOpened(Frame before, Frame after, out string why)
        {
            if (!Same(before, after)) { why = "no picture"; return null; }
            int band = BandRows(after);
            int bandTop = after.Height - band;
            double above = Changed(before, after, 0, bandTop - 4);
            if (above > 0.30) { why = $"whole picture changed ({above:P0})"; return null; }

            // Skip the left part: that is where the prompt and any text are drawn.
            Columns(after, 0.30, 0.97, 480, out int x0, out int x1, out int step);
            int bestTop = -1, bestEnd = -1, runTop = -1, runEnd = -1, gap = 0, barRows = 0;
            var hist = new int[256];
            var histBefore = new int[256];
            for (int y = bandTop; y < after.Height; y++)
            {
                Array.Clear(hist, 0, 256);
                Array.Clear(histBefore, 0, 256);
                int n = 0, changed = 0, darkerDen = 0, darker = 0;
                for (int x = x0; x < x1; x += step)
                {
                    int pb = before.L(x, y), pa = after.L(x, y);
                    n++;
                    hist[pa]++;
                    histBefore[pb]++;
                    if (Math.Abs(pa - pb) > 14) changed++;
                    if (pb >= 12)
                    {
                        darkerDen++;
                        if (pa <= pb * 0.85 && pb - pa >= 6) darker++;
                    }
                }
                double fFlat = Flatness(hist, n, 10, out int med);
                double fFlatBefore = Flatness(histBefore, n, 10, out _);
                double fChanged = (double)changed / n;
                double fDarker = darkerDen < n * 0.3 ? 0 : (double)darker / darkerDen;
                // A dark bar (solid or see-through) over a lighter picture, a solid line/bar replacing what was
                // there, or a dark but detailed picture (night) turning into flat black.
                bool isBar = (fDarker >= 0.80 && fChanged >= 0.45) || (fFlat >= 0.90 && fChanged >= 0.60)
                             || (fFlat >= 0.95 && med <= 16 && fFlatBefore < 0.75);
                if (isBar)
                {
                    barRows++;
                    if (runTop < 0) runTop = y;
                    runEnd = y;
                    gap = 0;
                }
                else if (runTop >= 0 && ++gap > 2)
                {
                    if (runEnd - runTop > bestEnd - bestTop) { bestTop = runTop; bestEnd = runEnd; }
                    runTop = -1; runEnd = -1; gap = 0;
                }
            }
            if (runTop >= 0 && runEnd - runTop > bestEnd - bestTop) { bestTop = runTop; bestEnd = runEnd; }

            int minRows = Math.Max(5, (int)Math.Round(after.ClientHeight * 0.006));
            int len = bestTop < 0 ? 0 : bestEnd - bestTop + 1;
            if (len < minRows) { why = $"no console line (bar rows {barRows}, best run {len}, need {minRows}, above {above:P0})"; return null; }
            // The line sits on the bottom edge of the picture.
            if (bestEnd < after.Height - 1 - Math.Max(6, band * 2 / 5)) { why = $"bar found at rows {bestTop}-{bestEnd}, not at the bottom edge"; return null; }
            why = $"console line rows {bestTop}-{bestEnd} of {after.Height}, above {above:P0}";
            return new ConsoleBar { Top = bestTop, Bottom = bestEnd };
        }

        private static int Percentile(int[] hist, int n, double q)
        {
            int target = (int)(n * q), acc = 0;
            for (int v = 0; v < 256; v++) { acc += hist[v]; if (acc > target) return v; }
            return 255;
        }

        /// <summary>Share of a row's samples within ±range of its median brightness.</summary>
        private static double Flatness(int[] hist, int n, int range, out int med)
        {
            med = 0;
            for (int acc = 0; med < 255 && (acc += hist[med]) < n / 2; med++) { }
            int flat = 0;
            for (int v = Math.Max(0, med - range); v <= Math.Min(255, med + range); v++) flat += hist[v];
            return n == 0 ? 0 : (double)flat / n;
        }

        /// <summary>
        /// Finds a console line that is already open, from one picture. The game draws it as a solid grey line
        /// (about 3 px at 1080p, the same value across the whole width) above a dark band down to the bottom edge.
        /// </summary>
        public static ConsoleBar FindOpenConsole(Frame f, out string why)
        {
            why = "no picture";
            if (f == null) return null;
            int band = BandRows(f);
            Columns(f, 0.30, 0.97, 480, out int x0, out int x1, out int step);
            var hist = new int[256];
            int darkTop = -1, mostlyDark = 0;
            // Dark rows up from the bottom edge. A long command covers much of the line with bright letters
            // (up to about half of a text row), so a row counts when its darker quarter is dark.
            for (int y = f.Height - 1; y >= f.Height - band; y--)
            {
                Array.Clear(hist, 0, 256);
                int n = 0;
                for (int x = x0; x < x1; x += step) { hist[f.L(x, y)]++; n++; }
                int p25 = Percentile(hist, n, 0.25), med = Percentile(hist, n, 0.5);
                if (p25 > 45) break;
                darkTop = y;
                if (med <= 45) mostlyDark++;
            }
            int minDark = Math.Max(6, (int)Math.Round(f.ClientHeight * 0.012));
            int rows = darkTop < 0 ? 0 : f.Height - darkTop;
            if (rows < minDark) { why = $"no dark console band ({rows} rows, need {minDark})"; return null; }
            if (mostlyDark < rows / 2) { why = $"dark band too busy ({mostlyDark} of {rows} rows mostly dark)"; return null; }
            // The solid border right above it.
            int borderRows = 0;
            for (int y = darkTop - 1; y >= Math.Max(0, darkTop - 6); y--)
            {
                Array.Clear(hist, 0, 256);
                int n = 0;
                for (int x = 0; x < f.Width; x += Math.Max(1, step)) { hist[f.L(x, y)]++; n++; }
                double flat = Flatness(hist, n, 4, out int med);
                if (flat >= 0.97 && med >= 80 && med <= 210) borderRows++; else break;
            }
            if (borderRows == 0) { why = "dark band without the console border"; return null; }
            why = $"console already open, rows {darkTop - borderRows}-{f.Height - 1}";
            return new ConsoleBar { Top = darkTop - borderRows, Bottom = f.Height - 1 };
        }

        /// <summary>
        /// True when the console line shows nothing past the prompt at its left end: no bright letter pixels in the
        /// dark band from 4% of the width onwards.
        /// </summary>
        public static bool LineLooksEmpty(Frame f, ConsoleBar bar)
        {
            if (f == null || bar == null) return false;
            int top = bar.Top + Math.Max(3, (bar.Bottom - bar.Top) / 5), bright = 0;
            for (int y = top; y <= bar.Bottom; y++)
                for (int x = (int)(f.Width * 0.04); x < f.Width; x++)
                    if (f.L(x, y) > 100 && ++bright > 6) return false;
            return true;
        }

        /// <summary>True when the bar still looks like it did right after the console opened (text may have changed).</summary>
        public static bool BarStillThere(Frame opened, Frame now, ConsoleBar bar)
        {
            if (!Same(opened, now) || bar == null) return false;
            return 1 - Changed(opened, now, bar.Top, bar.Bottom, 0.30, 0.97, 20) >= 0.55;
        }

        /// <summary>Did text show up in the console line?</summary>
        public static TextCheck TextIn(Frame empty, Frame now, ConsoleBar bar, out int textPixels)
        {
            textPixels = 0;
            if (!Same(empty, now) || bar == null) return TextCheck.Gone;
            int n = 0;
            for (int y = bar.Top; y <= bar.Bottom; y++)
                for (int x = 0; x < now.Width; x++)
                {
                    n++;
                    if (Math.Abs(empty.L(x, y) - now.L(x, y)) > 40) textPixels++;
                }
            double share = n == 0 ? 0 : (double)textPixels / n;
            if (share >= 0.5) return TextCheck.Gone;
            return textPixels >= 12 ? TextCheck.Appeared : TextCheck.None;
        }

        /// <summary>Share of the typed text's pixels still showing (1 = the line is unchanged, 0 = gone).</summary>
        public static double TextRemaining(Frame empty, Frame typed, Frame now, ConsoleBar bar)
        {
            if (!Same(empty, typed) || !Same(typed, now) || bar == null) return 0;
            int text = 0, kept = 0;
            for (int y = bar.Top; y <= bar.Bottom; y++)
                for (int x = 0; x < now.Width; x++)
                {
                    if (Math.Abs(empty.L(x, y) - typed.L(x, y)) <= 40) continue;
                    text++;
                    if (Math.Abs(typed.L(x, y) - now.L(x, y)) <= 25) kept++;
                }
            return text == 0 ? 0 : (double)kept / text;
        }
    }
}

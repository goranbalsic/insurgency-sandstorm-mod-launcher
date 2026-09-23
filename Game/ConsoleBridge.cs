using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SandstormModLauncher.Core;
using SandstormModLauncher.Models;

namespace SandstormModLauncher.Game
{
    public sealed class ConsoleKeyPlan
    {
        public string KeyName;       // Unreal key name that will be pressed
        public ushort VirtualKey;
        public bool Ok => VirtualKey != 0;
        public string Problem;
        public string Layout;
        public List<string> ActiveKeys = new List<string>();
        public bool ExtraKeyPending; // the launcher's extra key was written after the game started
    }

    public sealed class CommandResult
    {
        public bool Sent;            // Enter was pressed on a console line that was seen on screen
        public bool Verified;
        public bool NotRecognized;
        public bool NothingTyped;    // stopped before any key other than the console key reached the game
        public string Detail;
        public List<string> Lines = new List<string>();
    }

    internal sealed class ConsoleSendException : Exception
    {
        public bool NothingTyped { get; }
        public ConsoleSendException(string message, bool nothingTyped) : base(message) { NothingTyped = nothingTyped; }
    }

    /// <summary>
    /// Runs console commands on the player's behalf. Every step is checked on screen:
    /// the console key is pressed, and only when the console line is seen at the bottom of the
    /// game picture are the command and Enter sent. If anything looks off it stops, so keys can
    /// never land in the game's menus or key binding screens.
    /// </summary>
    public sealed class ConsoleBridge
    {
        private const ushort VK_BACK = 0x08, VK_RETURN = 0x0D, VK_ESCAPE = 0x1B, VK_END = 0x23, VK_CONTROL = 0x11, VK_V = 0x56;

        private readonly GameMonitor monitor;
        private readonly Func<AppSettings> settings;
        private readonly Func<OfficialData> official;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private int counter, savedOk;

        public ConsoleBridge(GameMonitor monitor, Func<AppSettings> settings, Func<OfficialData> official)
        {
            this.monitor = monitor;
            this.settings = settings;
            this.official = official;
        }

        public static string ShotsDir => Path.Combine(AppPaths.DataDir, "logs", "console");

        // ------------------------------------------------------------------ console keys (Input.ini)

        /// <summary>Console keys from DefaultInput.ini + the user's Input.ini.</summary>
        public static List<string> ConfiguredKeys(OfficialData od)
        {
            var defaults = od?.DefaultConsoleKeys?.Count > 0 ? od.DefaultConsoleKeys : new List<string> { "Tilde" };
            var keys = UeIni.ReadArray(UeIni.ReadText(GameInstall.InputIniPath), "/Script/Engine.InputSettings", "ConsoleKeys", defaults);
            return keys.Where(k => !k.Equals("None", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static bool AddConsoleKey(string key)
        {
            string path = GameInstall.InputIniPath;
            string text = UeIni.ReadText(path);
            string updated = UeIni.EnsureLine(text, "/Script/Engine.InputSettings", "+ConsoleKeys=" + key);
            if (updated == text) return false;
            BackupFile(path);
            UeIni.WriteText(path, updated);
            AppLog.Info("Input.ini: added " + key + " as a console key");
            return true;
        }

        /// <summary>
        /// Adds a function key as an extra console key while the game is closed. Function keys are the
        /// same on every keyboard layout, unlike the ` key which is missing on many non-English layouts.
        /// Returns the key that is (or already was) set up, or null.
        /// </summary>
        public static string EnsureLayoutFreeKey(OfficialData od, AppSettings s, bool gameRunning)
        {
            try
            {
                var keys = ConfiguredKeys(od);
                string have = keys.FirstOrDefault(IsFunctionKey);
                if (have != null) return have;
                if (gameRunning || AppPaths.TestRun) return null; // the game rewrites Input.ini when it exits
                foreach (var k in new[] { "F10", "F9", "F8", "F7" })
                {
                    if (KeyBindings.IsKeyBound(k)) { AppLog.Info("Console key: " + k + " is bound in the game's controls, trying another"); continue; }
                    if (AddConsoleKey(k)) { s.ConsoleKeyAddedUtc = DateTime.UtcNow; return k; }
                    return ConfiguredKeys(od).FirstOrDefault(IsFunctionKey);
                }
            }
            catch (Exception ex) { AppLog.Warn("Could not add a console key: " + ex.Message); }
            return null;
        }

        public static bool IsFunctionKey(string k) =>
            k != null && k.Length >= 2 && k.Length <= 3 && (k[0] == 'F' || k[0] == 'f') && int.TryParse(k.Substring(1), out int n) && n >= 1 && n <= 24;

        public static void BackupFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                string dir = Path.Combine(AppPaths.DataDir, "backups");
                Directory.CreateDirectory(dir);
                File.Copy(path, Path.Combine(dir, Path.GetFileName(path) + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak"), true);
                foreach (var old in Directory.GetFiles(dir, Path.GetFileName(path) + ".*.bak").OrderByDescending(f => f).Skip(15)) File.Delete(old);
            }
            catch (Exception ex) { AppLog.Warn("Backup failed: " + ex.Message); }
        }

        public static string LayoutName(IntPtr hkl)
        {
            try
            {
                int lcid = (int)((long)hkl & 0xFFFF);
                return lcid == 0 ? "unknown" : CultureInfo.GetCultureInfo(lcid).EnglishName;
            }
            catch { return "unknown"; }
        }

        public static string PrettyKey(string k) => k != null && k.Equals("Tilde", StringComparison.OrdinalIgnoreCase) ? "` (left of 1)" : k;

        public ConsoleKeyPlan PlanKey()
        {
            var plan = new ConsoleKeyPlan();
            if (monitor.Window == IntPtr.Zero) { plan.Problem = "The game window was not found."; return plan; }
            var input = new GameInput(monitor.Window);
            plan.Layout = LayoutName(input.KeyboardLayout);
            plan.ActiveKeys = ConfiguredKeys(official());
            // A key written to Input.ini only works after the game restarts.
            bool addedAfterStart = monitor.ProcessStartUtc.HasValue && settings().ConsoleKeyAddedUtc > monitor.ProcessStartUtc.Value;
            var usable = new List<string>();
            foreach (var k in plan.ActiveKeys)
            {
                bool isDefault = official()?.DefaultConsoleKeys?.Contains(k, StringComparer.OrdinalIgnoreCase) == true;
                if (!isDefault && addedAfterStart && IsFunctionKey(k)) { plan.ExtraKeyPending = true; continue; }
                usable.Add(k);
            }
            // Function keys do not depend on the keyboard layout, so they go first.
            foreach (var k in usable.OrderBy(k => IsFunctionKey(k) ? 0 : 1))
            {
                ushort vk = input.VirtualKeyFor(k);
                if (vk == 0) continue;
                plan.KeyName = k; plan.VirtualKey = vk;
                return plan;
            }
            string keys = string.Join(", ", usable.Select(PrettyKey));
            plan.Problem = plan.ExtraKeyPending
                ? $"The {plan.Layout} keyboard layout has no {keys} key, and F10 (added for this) only works after the game restarts. Close the game and launch from here, or switch the keyboard to English (Win+Space) for now."
                : $"The {plan.Layout} keyboard layout has no {keys} key, which the game uses to open its console. Close the game and launch from here (the launcher then adds F10, which works on every layout), or switch the keyboard to English (Win+Space).";
            return plan;
        }

        // ------------------------------------------------------------------ running commands

        /// <summary>Runs one console line. Multiple commands can be joined with " | ".</summary>
        public async Task<CommandResult> Run(string commandLine, CancellationToken ct, Func<string, bool> success = null, TimeSpan? verifyTimeout = null)
        {
            var result = new CommandResult();
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                int id = Interlocked.Increment(ref counter);
                string shown = commandLine.Length > 300 ? commandLine.Substring(0, 300) + "..." : commandLine;
                var plan = PlanKey();
                if (!plan.Ok)
                {
                    result.Detail = plan.Problem;
                    result.NothingTyped = true;
                    AppLog.Warn($"Console #{id} not sent: {plan.Problem}");
                    return result;
                }
                KeyBindings.Backup("before console #" + id);
                var words = commandLine.Split('|').Select(p => p.Trim().Split(' ')[0]).ToList();
                var cfg = settings();
                var lines = new List<string>();
                void Collect(string l) { lock (lines) lines.Add(l); }
                monitor.LineReceived += Collect;
                try
                {
                    Exception error = null;
                    string trace = null;
                    await Task.Run(() =>
                    {
                        try { trace = Deliver(id, commandLine, plan, cfg); }
                        catch (Exception ex) { error = ex; }
                    }).ConfigureAwait(false);
                    if (error != null)
                    {
                        result.Detail = error.Message;
                        result.NothingTyped = (error as ConsoleSendException)?.NothingTyped ?? false;
                        AppLog.Warn($"Console #{id} [{plan.KeyName}, {plan.Layout}] failed: {error.Message} | {shown}");
                        return result;
                    }
                    result.Sent = true;

                    var deadline = DateTime.UtcNow + (verifyTimeout ?? TimeSpan.FromSeconds(3));
                    while (DateTime.UtcNow < deadline)
                    {
                        await Task.Delay(150, ct).ConfigureAwait(false);
                        monitor.Poll();
                        List<string> snapshot;
                        lock (lines) snapshot = new List<string>(lines);
                        string bad = snapshot.FirstOrDefault(l => l.Contains("Command not recognized: "));
                        if (bad != null)
                        {
                            string what = bad.Substring(bad.IndexOf("Command not recognized: ", StringComparison.Ordinal) + 24).Trim();
                            result.NotRecognized = true;
                            bool ours = words.Any(w => w.Length > 0 && what.StartsWith(w, StringComparison.OrdinalIgnoreCase));
                            result.Detail = "The game did not recognise \"" + what + "\"." +
                                (ours ? "" : " Keys pressed at the same moment may have mixed into the command.");
                            break;
                        }
                        if (success != null && snapshot.Any(success)) { result.Verified = true; break; }
                    }
                    lock (lines) result.Lines = new List<string>(lines);
                    if (success == null) result.Verified = !result.NotRecognized;
                    AppLog.Info($"Console #{id} [{plan.KeyName}] {(result.Verified ? "ok" : result.NotRecognized ? "not recognised" : "sent, no confirmation in the log")}: {shown} ({trace})");
                }
                finally { monitor.LineReceived -= Collect; }
                return result;
            }
            finally { gate.Release(); }
        }

        private static bool ExpectsFreeze(string commandLine)
        {
            foreach (var part in commandLine.Split('|'))
            {
                string w = part.Trim().Split(' ')[0].ToLowerInvariant();
                if (w == "open" || w == "travel" || w == "servertravel" || w == "exit" || w == "quit" || w == "restartlevel" ||
                    w == "disconnect" || w == "reconnect" || w == "adminchangemap") return true;
            }
            return false;
        }

        /// <summary>Name of a keyboard key the player is holding, or null when the keyboard is idle.</summary>
        private static string HeldKey()
        {
            for (int vk = 0x08; vk <= 0xFE; vk++)
                if ((Native.GetAsyncKeyState(vk) & 0x8000) != 0) return "key 0x" + vk.ToString("X2");
            return null;
        }

        private static string WaitForKeysReleased(int timeoutMs)
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            string held;
            while ((held = HeldKey()) != null && DateTime.UtcNow < until) Thread.Sleep(40);
            return held;
        }

        private static Frame Grab(IntPtr hwnd) => ScreenGrab.BottomOfWindow(hwnd);

        /// <summary>
        /// A picture of the bottom of the game once it shows something and stops changing (or the latest
        /// one after the timeout). Null when the picture stays empty, e.g. fullscreen never came back.
        /// </summary>
        private static Frame WaitSteady(IntPtr hwnd, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            Frame prev = Grab(hwnd);
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Thread.Sleep(90);
                var cur = Grab(hwnd);
                if (cur == null || ConsoleProbe.Blank(cur)) continue;
                if (prev != null && !ConsoleProbe.Blank(prev) && ConsoleProbe.Steady(prev, cur)) return cur;
                prev = cur;
            }
            return prev == null || ConsoleProbe.Blank(prev) ? null : prev;
        }

        private void SaveShots(int id, string tag, params Frame[] frames)
        {
            try
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                for (int i = 0; i < frames.Length; i++)
                    frames[i]?.SavePng(Path.Combine(ShotsDir, $"{stamp}-{id:000}-{tag}-{i}.png"));
                foreach (var old in Directory.GetFiles(ShotsDir, "*.png").OrderByDescending(f => f).Skip(90)) File.Delete(old);
            }
            catch { }
        }

        /// <summary>Sends one line. Throws ConsoleSendException (with what was and was not sent) on any doubt.</summary>
        private string Deliver(int id, string commandLine, ConsoleKeyPlan plan, AppSettings cfg)
        {
            IntPtr hwnd = monitor.Window;
            var input = new GameInput(hwnd) { KeyDelayMs = Math.Max(30, cfg.KeyDelayMs) };
            var log = new StringBuilder();
            var total = Stopwatch.StartNew();
            void Step(string s) => log.Append(log.Length == 0 ? "" : "; ").Append(total.ElapsedMilliseconds).Append("ms ").Append(s);

            // Keys the player still holds would end up in the command.
            string held = WaitForKeysReleased(3000);
            if (held != null) throw new ConsoleSendException("A keyboard key is held down (" + held + "). Let go of the keyboard and try again.", true);
            bool wasInFront = input.IsForeground;
            if (!input.Focus(4000)) throw new ConsoleSendException("Could not bring the game to the front. Click the game once and try again.", true);
            // Coming back from the background, fullscreen needs a moment before the game draws again.
            Thread.Sleep(wasInFront ? 150 : 700);
            Frame before = WaitSteady(hwnd, 3500);
            if (before == null) throw new ConsoleSendException("Could not see the game picture (it stayed empty), so no keys were sent. Click the game once and try again.", true);
            if (!input.IsForeground) throw new ConsoleSendException("The game lost focus before the console could be opened, so no keys were sent.", true);
            held = WaitForKeysReleased(1500);
            if (held != null) throw new ConsoleSendException("A keyboard key is held down (" + held + "). Let go of the keyboard and try again.", true);
            Step("focused");

            // 1. Open the console and see it on screen.
            input.Tap(plan.VirtualKey);
            ConsoleBar bar = null;
            Frame opened = null, last = null;
            string why = "no picture";
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1500)
            {
                Thread.Sleep(50);
                var f = Grab(hwnd);
                if (f == null) continue;
                last = f;
                bar = ConsoleProbe.DetectOpened(before, f, out why);
                if (bar != null) { opened = f; break; }
            }
            if (bar == null)
            {
                SaveShots(id, "not-open", before, last);
                throw new ConsoleSendException("The game's console did not open when " + PrettyKey(plan.KeyName) + " was pressed, so nothing was typed (" + why + "). " +
                                               "If a menu, message or loading screen is showing in the game, close it and try again.", true);
            }
            Step("console open " + bar + " (" + why + ")");
            Thread.Sleep(40);
            var settled = Grab(hwnd);
            if (settled != null && ConsoleProbe.BarStillThere(opened, settled, bar)) opened = settled;

            // 2. Clear anything left on the line, checking the console is still open in between.
            input.Tap(VK_END);
            for (int i = 0; i < 5; i++)
            {
                var f = Grab(hwnd);
                if (!ConsoleProbe.BarStillThere(opened, f, bar))
                {
                    SaveShots(id, "closed-while-clearing", before, opened, f);
                    throw new ConsoleSendException("The console closed while the launcher was using it, so the command was not sent.", false);
                }
                input.TapRepeat(VK_BACK, 30);
            }
            Thread.Sleep(60);
            Frame empty = Grab(hwnd);
            if (!ConsoleProbe.BarStillThere(opened, empty, bar))
            {
                SaveShots(id, "closed-after-clearing", before, opened, empty);
                throw new ConsoleSendException("The console closed while the launcher was using it, so the command was not sent.", false);
            }
            Step("line cleared");

            // 3. Put the command on the line and see it appear.
            bool usePaste = !string.Equals(cfg.InputMethod, "Type", StringComparison.OrdinalIgnoreCase);
            Frame typed = null;
            TextCheck check = TextCheck.None;
            int textPixels = 0;
            if (usePaste)
            {
                string savedClipboard = null;
                try
                {
                    savedClipboard = OnUi(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
                    OnUi(() => { SetClipboard(commandLine); return true; });
                    Thread.Sleep(60);
                    if (HeldKey() != null) { WaitForKeysReleased(1500); input.TapRepeat(VK_BACK, 30); }
                    input.Chord(VK_CONTROL, VK_V);
                    check = WaitForText(hwnd, empty, bar, 900, out typed, out textPixels);
                }
                finally
                {
                    try { OnUi(() => { if (savedClipboard != null) SetClipboard(savedClipboard); else Clipboard.Clear(); return true; }); } catch { }
                }
                Step("pasted: " + check + " (" + textPixels + " px)");
            }
            if (check == TextCheck.None)
            {
                var f = Grab(hwnd);
                if (ConsoleProbe.BarStillThere(opened, f, bar) && ConsoleProbe.TextIn(empty, f, bar, out _) == TextCheck.None)
                {
                    input.TypeText(commandLine);
                    Thread.Sleep(120);
                    check = WaitForText(hwnd, empty, bar, 900, out typed, out textPixels);
                    Step("typed: " + check + " (" + textPixels + " px)");
                }
            }
            if (check != TextCheck.Appeared)
            {
                var f = Grab(hwnd);
                SaveShots(id, "no-text", before, opened, empty, f);
                // An empty open console closes with one Escape; only press it when the empty line is certainly showing.
                if (check == TextCheck.None && ConsoleProbe.BarStillThere(opened, f, bar) && ConsoleProbe.TextIn(empty, f, bar, out _) == TextCheck.None)
                {
                    input.Tap(VK_ESCAPE);
                    Step("closed the empty console");
                }
                throw new ConsoleSendException(check == TextCheck.Gone ? "The console closed before the command could be sent." : "The command could not be put on the console line, so it was not sent.", false);
            }
            // Let the whole line draw, then make sure nothing changed before pressing Enter.
            Thread.Sleep(70);
            var full = Grab(hwnd);
            if (full != null && ConsoleProbe.TextIn(empty, full, bar, out int px2) == TextCheck.Appeared && px2 >= textPixels) { typed = full; textPixels = px2; }
            var pre = Grab(hwnd);
            double keep = ConsoleProbe.TextRemaining(empty, typed, pre, bar);
            if (keep < 0.8)
            {
                SaveShots(id, "changed-before-enter", before, opened, typed, pre);
                throw new ConsoleSendException("The console line changed before Enter, so Enter was not pressed.", false);
            }

            // 4. Enter, then see the line go away.
            input.Tap(VK_RETURN);
            Step("enter");
            bool freeze = ExpectsFreeze(commandLine);
            double remaining = 1;
            Frame after = null;
            sw.Restart();
            while (sw.ElapsedMilliseconds < (freeze ? 1500 : 3000))
            {
                Thread.Sleep(80);
                var f = Grab(hwnd);
                if (f == null) continue;
                after = f;
                remaining = ConsoleProbe.TextRemaining(empty, typed, f, bar);
                if (remaining < 0.5) break;
            }
            if (remaining < 0.5)
            {
                Step("line gone after " + sw.ElapsedMilliseconds + "ms");
                if (Interlocked.Increment(ref savedOk) <= 2) SaveShots(id, "ok", before, opened, typed, after);
                return log.ToString();
            }
            if (freeze)
            {
                // A map change stops the picture while it loads; the game log tells whether it worked.
                Step("line still on screen (game busy)");
                return log.ToString();
            }
            Thread.Sleep(250);
            var later = Grab(hwnd);
            SaveShots(id, "enter-ignored", before, opened, typed, after, later);
            if (ConsoleProbe.Alive(after, later) && ConsoleProbe.BarStillThere(opened, later, bar) && ConsoleProbe.TextRemaining(empty, typed, later, bar) >= 0.5)
            {
                // The game is drawing frames and the text is still on the open line: clear it, then close the console.
                input.Tap(VK_ESCAPE);
                Thread.Sleep(150);
                var cleared = Grab(hwnd);
                if (ConsoleProbe.BarStillThere(opened, cleared, bar) && ConsoleProbe.TextIn(empty, cleared, bar, out _) == TextCheck.None) input.Tap(VK_ESCAPE);
                Step("enter ignored; line cleared");
            }
            throw new ConsoleSendException("The game did not take the Enter key. The console line was cleared so nothing is left typed in it.", false);
        }

        private static TextCheck WaitForText(IntPtr hwnd, Frame empty, ConsoleBar bar, int timeoutMs, out Frame typed, out int textPixels)
        {
            var sw = Stopwatch.StartNew();
            typed = null;
            textPixels = 0;
            TextCheck check = TextCheck.None;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Thread.Sleep(50);
                var f = Grab(hwnd);
                if (f == null) continue;
                check = ConsoleProbe.TextIn(empty, f, bar, out textPixels);
                typed = f;
                if (check != TextCheck.None) break;
            }
            return check;
        }

        private static void SetClipboard(string text)
        {
            for (int i = 0; i < 10; i++)
            {
                try { Clipboard.SetDataObject(text, true); return; }
                catch { Thread.Sleep(50); }
            }
            Clipboard.SetText(text);
        }

        private static T OnUi<T>(Func<T> f)
        {
            var d = Application.Current?.Dispatcher;
            if (d == null || d.CheckAccess()) return f();
            return d.Invoke(f);
        }

        /// <summary>Reads live values: getall &lt;Class&gt; &lt;Prop&gt; for every property.</summary>
        public async Task<Dictionary<string, string>> ReadLive(string className, IEnumerable<string> props, CancellationToken ct)
        {
            var list = props.ToList();
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < list.Count; i += 40)
            {
                var chunk = list.Skip(i).Take(40).ToList();
                string cmd = string.Join(" | ", chunk.Select(p => "getall " + className + " " + p));
                var res = await Run(cmd, ct, null, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                foreach (var l in res.Lines)
                {
                    foreach (var p in chunk)
                    {
                        int idx = l.IndexOf("." + p + " = ", StringComparison.Ordinal);
                        if (idx >= 0 && l.IndexOf("PersistentLevel", StringComparison.Ordinal) >= 0) values[p] = l.Substring(idx + p.Length + 4).Trim();
                    }
                }
                if (!res.Sent) break;
            }
            return values;
        }
    }
}

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
        public string AltKeyName;    // second console key, tried when the first press shows nothing
        public ushort AltVirtualKey;
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
            // The game's own key (`/~) first when this keyboard layout has it, the layout-free function key otherwise;
            // the other one is kept for a second try.
            foreach (var k in usable.OrderBy(k => IsFunctionKey(k) ? 1 : 0))
            {
                ushort vk = input.VirtualKeyFor(k);
                if (vk == 0) continue;
                if (plan.VirtualKey == 0) { plan.KeyName = k; plan.VirtualKey = vk; }
                else if (vk != plan.VirtualKey) { plan.AltKeyName = k; plan.AltVirtualKey = vk; break; }
            }
            if (plan.Ok) return plan;
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
                // The console cannot open while the loading picture covers the game.
                var waitLoading = Stopwatch.StartNew();
                while (monitor.LoadingScreenUp && waitLoading.Elapsed < TimeSpan.FromSeconds(30))
                {
                    await Task.Delay(250, ct).ConfigureAwait(false);
                    monitor.Poll();
                }
                if (monitor.LoadingScreenUp)
                {
                    result.Detail = "The game is still showing its loading screen, so the console cannot open. Try again once it is gone.";
                    result.NothingTyped = true;
                    AppLog.Warn($"Console #{id} not sent: loading screen still up after 30 s");
                    return result;
                }
                if (waitLoading.ElapsedMilliseconds > 300) await Task.Delay(1200, ct).ConfigureAwait(false);
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

        // Keys Windows reports as held although nobody is pressing them (a remapping tool, a controller mapped to
        // keys, a key-up that never arrived). Found per send; they are skipped by HeldKey.
        private readonly HashSet<int> stuckKeys = new HashSet<int>();

        // Codes that are not typing keys: reserved, gamepad buttons (0xC3-0xDA), IME/OEM-specific.
        private static bool NotAKey(int vk) =>
            vk == 0x0A || vk == 0x0B || vk == 0x5E || (vk >= 0x88 && vk <= 0x8F) || (vk >= 0xC1 && vk <= 0xDA) || vk == 0xE5 || vk == 0xE7 || vk >= 0xE9;

        // Held modifiers change what every other key does, so they always have to be let go.
        private static bool IsModifier(int vk) => (vk >= 0x10 && vk <= 0x12) || (vk >= 0xA0 && vk <= 0xA5) || vk == 0x5B || vk == 0x5C;

        public static string KeyName(int vk)
        {
            if ((vk >= 0x30 && vk <= 0x39) || (vk >= 0x41 && vk <= 0x5A)) return ((char)vk).ToString();
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);
            switch (vk)
            {
                case 0x10: case 0xA0: case 0xA1: return "Shift";
                case 0x11: case 0xA2: case 0xA3: return "Ctrl";
                case 0x12: case 0xA4: case 0xA5: return "Alt";
                case 0x5B: case 0x5C: return "Windows";
                case 0x20: return "Space";
                case 0x0D: return "Enter";
                case 0x09: return "Tab";
                case 0x1B: return "Esc";
                default: return "0x" + vk.ToString("X2");
            }
        }

        /// <summary>A keyboard key the player is holding (not one found stuck), or 0 when the keyboard is idle.</summary>
        private int HeldKey()
        {
            for (int vk = 0x08; vk <= 0xFE; vk++)
                if (!NotAKey(vk) && !stuckKeys.Contains(vk) && (Native.GetAsyncKeyState(vk) & 0x8000) != 0) return vk;
            return 0;
        }

        private static string Describe(int vk) => "key " + KeyName(vk) + " (0x" + vk.ToString("X2") + ")";

        private static string HeldMessage(string held) =>
            "Windows reports a keyboard " + held + " as held down, so nothing was typed (it would mix into the command). " +
            "Let go of the keyboard and try again. If you are not pressing it, a key remapping tool, macro software or a " +
            "controller mapped to keys may be holding it: tap that key once, or click Launch while the launcher window is in front.";

        /// <summary>
        /// Waits for the keyboard to be idle. A non-modifier key still down after the wait while the game is NOT in
        /// front is taken as stuck (the player is at the launcher, not holding a key for seconds) and ignored from
        /// then on. Returns the held key's description when the send has to stop, else null.
        /// </summary>
        private string WaitForKeysReleased(int timeoutMs, bool gameInFront)
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            int held;
            while ((held = HeldKey()) != 0 && DateTime.UtcNow < until) Thread.Sleep(40);
            while (held != 0 && !gameInFront && !IsModifier(held))
            {
                stuckKeys.Add(held);
                AppLog.Warn("Console: Windows reports " + Describe(held) + " as held for " + timeoutMs / 1000.0 +
                            " s while the launcher was in front; treated as stuck and ignored");
                held = HeldKey();
            }
            return held == 0 ? null : Describe(held);
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
            stuckKeys.Clear();
            bool wasInFront = input.IsForeground;
            string held = WaitForKeysReleased(3000, wasInFront);
            if (held != null) throw new ConsoleSendException(HeldMessage(held), true);
            if (!input.Focus(4000)) throw new ConsoleSendException("Could not bring the game to the front. Click the game once and try again.", true);
            // Coming back from the background, fullscreen needs a moment before the game draws again.
            Thread.Sleep(wasInFront ? 120 : 1000);
            Frame before = WaitSteady(hwnd, 3500);
            if (before == null) throw new ConsoleSendException("Could not see the game picture (it stayed empty), so no keys were sent. Click the game once and try again.", true);
            if (!input.IsForeground) throw new ConsoleSendException("The game lost focus before the console could be opened, so no keys were sent.", true);
            held = WaitForKeysReleased(1500, true);
            if (held != null) throw new ConsoleSendException(HeldMessage(held), true);
            Step("focused" + (stuckKeys.Count > 0 ? " (ignored stuck " + string.Join(", ", stuckKeys.Select(KeyName)) + ")" : ""));

            // 1. Open the console and see it on screen (or use it when it is already open).
            ConsoleBar bar = ConsoleProbe.FindOpenConsole(before, out string why);
            Frame opened = bar != null ? before : null, last = before;
            var sw = Stopwatch.StartNew();
            string pressed = "";
            ushort openVk = plan.VirtualKey;
            Frame reference = before;
            foreach (var (keyName, vk) in new[] { (plan.KeyName, plan.VirtualKey), (plan.AltKeyName ?? plan.KeyName, plan.AltKeyName != null ? plan.AltVirtualKey : plan.VirtualKey) })
            {
                if (bar != null) break;
                if (pressed.Length > 0)
                {
                    // Nothing showed after the first key: look once more (a late frame), then try again.
                    Thread.Sleep(400);
                    var late = Grab(hwnd);
                    bar = ConsoleProbe.FindOpenConsole(late, out why);
                    if (bar != null) { opened = late; break; }
                    if (HeldKey() != 0) break;
                    reference = late ?? reference;
                }
                input.Tap(vk);
                openVk = vk;
                pressed += (pressed.Length > 0 ? ", then " : "") + PrettyKey(keyName);
                sw.Restart();
                while (sw.ElapsedMilliseconds < 1500)
                {
                    Thread.Sleep(50);
                    var f = Grab(hwnd);
                    if (f == null) continue;
                    last = f;
                    bar = ConsoleProbe.DetectOpened(reference, f, out why) ?? ConsoleProbe.FindOpenConsole(f, out _);
                    if (bar != null) { opened = f; break; }
                }
            }
            if (bar == null)
            {
                SaveShots(id, "not-open", before, last);
                throw new ConsoleSendException("The game's console did not open when " + pressed + " was pressed, so nothing was typed (" + why + "). " +
                                               "If a menu, message or loading screen is showing in the game, close it and try again.", true);
            }
            Step((pressed.Length > 0 ? "console opened with " + pressed : "console was already open") + " " + bar + " (" + why + ")");
            Thread.Sleep(40);
            var settled = Grab(hwnd);
            if (settled != null && ConsoleProbe.BarStillThere(opened, settled, bar)) opened = settled;

            // 2. Clear anything left on the line, checking the console is still open in between.
            // Stops as soon as the line shows nothing past the prompt (usually after the first batch).
            input.Tap(VK_END);
            for (int i = 0; i < 6; i++)
            {
                var f = Grab(hwnd);
                if (!ConsoleProbe.BarStillThere(opened, f, bar))
                {
                    SaveShots(id, "closed-while-clearing", before, opened, f);
                    throw new ConsoleSendException("The console closed while the launcher was using it, so the command was not sent.", false);
                }
                if (i > 0 && ConsoleProbe.LineLooksEmpty(f, bar)) break;
                input.TapRepeat(VK_BACK, 40);
            }
            Thread.Sleep(40);
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
            int textPixels = 0;

            // Pastes (or types) the command onto an open, empty console line and checks it shows.
            TextCheck PutLine(Frame emptyLine, out Frame shown, out int px)
            {
                TextCheck result = TextCheck.None;
                shown = null; px = 0;
                if (usePaste)
                {
                    string savedClipboard = null;
                    try
                    {
                        savedClipboard = OnUi(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
                        OnUi(() => { SetClipboard(commandLine); return true; });
                        Thread.Sleep(60);
                        if (HeldKey() != 0) { WaitForKeysReleased(1500, true); input.TapRepeat(VK_BACK, 30); }
                        input.Chord(VK_CONTROL, VK_V);
                        result = WaitForText(hwnd, emptyLine, bar, 900, out shown, out px);
                    }
                    finally
                    {
                        try { OnUi(() => { if (savedClipboard != null) SetClipboard(savedClipboard); else Clipboard.Clear(); return true; }); } catch { }
                    }
                    Step("pasted: " + result + " (" + px + " px)");
                }
                if (result == TextCheck.None)
                {
                    var f = Grab(hwnd);
                    if (ConsoleProbe.BarStillThere(opened, f, bar) && ConsoleProbe.TextIn(emptyLine, f, bar, out _) == TextCheck.None)
                    {
                        input.TypeText(commandLine);
                        Thread.Sleep(120);
                        result = WaitForText(hwnd, emptyLine, bar, 900, out shown, out px);
                        Step("typed: " + result + " (" + px + " px)");
                    }
                }
                if (result == TextCheck.Appeared)
                {
                    // Let the whole line draw.
                    Thread.Sleep(70);
                    var full = Grab(hwnd);
                    if (full != null && ConsoleProbe.TextIn(emptyLine, full, bar, out int more) == TextCheck.Appeared && more >= px) { shown = full; px = more; }
                }
                return result;
            }

            TextCheck check = PutLine(empty, out typed, out textPixels);
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

            // 4. The game's menus can take the keyboard back after the console opened (window activation,
            // menu set-up). Other keys still reach the console then, but Enter presses the focused menu button
            // instead of running the line. The console takes the keyboard whenever it opens, so cycle it
            // (typing -> big console -> closed -> typing, one quick key press each) right before Enter,
            // and check the line is still there.
            bool reopened = false;
            for (int press = 0; press < 5 && !reopened; press++)
            {
                input.Tap(openVk);
                Frame f = null;
                ConsoleBar now = null;
                var wait = Stopwatch.StartNew();
                // First press: wait for the line to go (big console). Second: the console closes, which looks the
                // same at the bottom edge, so only a short look. After that: wait for the line to come back.
                bool wantOpen = press > 0;
                int timeout = press == 0 ? 500 : press == 1 ? 90 : 900;
                do
                {
                    Thread.Sleep(30);
                    f = Grab(hwnd);
                    now = ConsoleProbe.FindOpenConsole(f, out _);
                    if ((now != null) == wantOpen) break;
                }
                while (wait.ElapsedMilliseconds < timeout);
                if (!wantOpen || now == null) continue;  // the next press moves the cycle on
                reopened = true;
                bar = now;
                opened = f;
                Thread.Sleep(60);
                var back = Grab(hwnd);
                double kept = ConsoleProbe.TextRemaining(empty, typed, back, bar);
                Step("console opened again for Enter after " + (press + 1) + " key presses, line kept " + kept.ToString("0.00"));
                if (kept < 0.8)
                {
                    // The game cleared the line when it closed: clear leftovers and put the command back.
                    input.Tap(VK_END);
                    input.TapRepeat(VK_BACK, 30);
                    for (int i = 0; i < 4; i++) { if (!ConsoleProbe.BarStillThere(opened, Grab(hwnd), bar)) break; input.TapRepeat(VK_BACK, 30); }
                    Thread.Sleep(60);
                    empty = Grab(hwnd);
                    if (!ConsoleProbe.BarStillThere(opened, empty, bar))
                        throw new ConsoleSendException("The console closed while the launcher was using it, so the command was not sent.", false);
                    if (PutLine(empty, out typed, out textPixels) != TextCheck.Appeared)
                    {
                        SaveShots(id, "no-text-after-reopen", before, opened, empty, Grab(hwnd));
                        throw new ConsoleSendException("The command could not be put back on the console line, so it was not sent.", false);
                    }
                }
            }
            if (!reopened)
            {
                SaveShots(id, "reopen-failed", before, opened, typed, Grab(hwnd));
                throw new ConsoleSendException("The console could not be opened again right before Enter, so Enter was not pressed. Click once into the game and try again.", false);
            }
            var pre = Grab(hwnd);
            double keep = ConsoleProbe.TextRemaining(empty, typed, pre, bar);
            if (keep < 0.8)
            {
                SaveShots(id, "changed-before-enter", before, opened, typed, pre);
                throw new ConsoleSendException("The console line changed before Enter, so Enter was not pressed.", false);
            }

            // 5. Enter, then see the line go away.
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        public List<string> ActiveKeys = new List<string>();
        public bool F10Pending;      // F10 written to Input.ini after the game started
    }

    public sealed class CommandResult
    {
        public bool Sent;
        public bool Verified;
        public bool NotRecognized;
        public string Detail;
        public List<string> Lines = new List<string>();
    }

    /// <summary>
    /// Delivers commands to the game console on the player's behalf: focuses the game, opens the
    /// console, pastes (or types) the command and presses Enter, then checks the log.
    /// </summary>
    public sealed class ConsoleBridge
    {
        private readonly GameMonitor monitor;
        private readonly Func<AppSettings> settings;
        private readonly Func<OfficialData> official;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

        public ConsoleBridge(GameMonitor monitor, Func<AppSettings> settings, Func<OfficialData> official)
        {
            this.monitor = monitor;
            this.settings = settings;
            this.official = official;
        }

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
            return true;
        }

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

        public ConsoleKeyPlan PlanKey()
        {
            var plan = new ConsoleKeyPlan();
            if (monitor.Window == IntPtr.Zero) { plan.Problem = "The game window was not found."; return plan; }
            var input = new GameInput(monitor.Window);
            plan.ActiveKeys = ConfiguredKeys(official());
            // A key the launcher added only works after the game restarts.
            bool addedAfterStart = monitor.ProcessStartUtc.HasValue && settings().ConsoleKeyAddedUtc > monitor.ProcessStartUtc.Value;
            var usable = new List<string>();
            foreach (var k in plan.ActiveKeys)
            {
                bool isDefault = official()?.DefaultConsoleKeys?.Contains(k, StringComparer.OrdinalIgnoreCase) == true;
                if (!isDefault && addedAfterStart) { if (k.Equals("F10", StringComparison.OrdinalIgnoreCase)) plan.F10Pending = true; continue; }
                usable.Add(k);
            }
            string preferred = settings().ConsoleKey;
            IEnumerable<string> order = preferred != null && !preferred.Equals("Auto", StringComparison.OrdinalIgnoreCase)
                ? new[] { preferred }.Concat(usable)
                : usable.OrderBy(k => k.StartsWith("F", StringComparison.OrdinalIgnoreCase) && k.Length <= 3 ? 0 : 1);
            foreach (var k in order)
            {
                if (!usable.Contains(k, StringComparer.OrdinalIgnoreCase)) continue;
                ushort vk = input.VirtualKeyFor(k);
                if (vk == 0) continue;
                plan.KeyName = k; plan.VirtualKey = vk;
                return plan;
            }
            plan.Problem = plan.F10Pending
                ? "F10 was added as a console key after the game started. Restart the game once so it takes effect."
                : "None of the console keys (" + string.Join(", ", plan.ActiveKeys) + ") can be pressed on your current keyboard layout. Use Settings > Console key > Set up F10.";
            return plan;
        }

        /// <summary>Runs one console line. Multiple commands can be joined with " | ".</summary>
        public async Task<CommandResult> Run(string commandLine, CancellationToken ct, Func<string, bool> success = null, TimeSpan? verifyTimeout = null)
        {
            var result = new CommandResult();
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var plan = PlanKey();
                if (!plan.Ok) { result.Detail = plan.Problem; return result; }
                string firstWord = commandLine.Trim().Split(' ', '|')[0];
                var cfg = settings();
                var lines = new List<string>();
                void Collect(string l) { lock (lines) lines.Add(l); }
                monitor.LineReceived += Collect;
                try
                {
                    Exception error = null;
                    await Task.Run(() =>
                    {
                        try { Deliver(commandLine, plan, cfg); }
                        catch (Exception ex) { error = ex; }
                    }, ct).ConfigureAwait(false);
                    if (error != null) { result.Detail = error.Message; return result; }
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
                            result.Detail = "The game did not recognise \"" + what + "\"." +
                                (what.Equals(firstWord, StringComparison.OrdinalIgnoreCase) ? "" : " Keys pressed at the same moment may have mixed into the command.");
                            break;
                        }
                        if (success != null && snapshot.Any(success)) { result.Verified = true; break; }
                    }
                    lock (lines) result.Lines = new List<string>(lines);
                    if (success == null) result.Verified = result.Sent && !result.NotRecognized;
                }
                finally { monitor.LineReceived -= Collect; }
                return result;
            }
            finally { gate.Release(); }
        }

        /// <summary>True while the player holds any keyboard key (mouse buttons are ignored).</summary>
        private static bool AnyKeyHeld()
        {
            for (int vk = 0x08; vk <= 0xFE; vk++)
                if ((Native.GetAsyncKeyState(vk) & 0x8000) != 0) return true;
            return false;
        }

        private static void WaitForKeysReleased(int timeoutMs)
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (AnyKeyHeld() && DateTime.UtcNow < until) Thread.Sleep(40);
        }

        private void Deliver(string commandLine, ConsoleKeyPlan plan, AppSettings cfg)
        {
            var input = new GameInput(monitor.Window) { KeyDelayMs = Math.Max(30, cfg.KeyDelayMs) };
            // Keys the player is still holding (e.g. moving in game) would end up in the console line.
            WaitForKeysReleased(3000);
            if (!input.Focus(4000)) throw new InvalidOperationException("Could not bring the game window to the front. Click the game once and try again.");
            Thread.Sleep(250);
            string savedClipboard = null;
            bool usePaste = !string.Equals(cfg.InputMethod, "Type", StringComparison.OrdinalIgnoreCase);
            try
            {
                input.Tap(plan.VirtualKey);
                Thread.Sleep(220);
                input.ClearLine();
                if (usePaste)
                {
                    savedClipboard = OnUi(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
                    OnUi(() => { SetClipboard(commandLine); return true; });
                    Thread.Sleep(60);
                    if (AnyKeyHeld()) { WaitForKeysReleased(1500); input.ClearLine(); }
                    input.Chord(0x11, 0x56);
                }
                else
                {
                    if (AnyKeyHeld()) { WaitForKeysReleased(1500); input.ClearLine(); }
                    input.TypeText(commandLine);
                }
                Thread.Sleep(80);
                input.Tap(0x0D);
                Thread.Sleep(250);
            }
            finally
            {
                if (usePaste)
                {
                    try { OnUi(() => { if (savedClipboard != null) SetClipboard(savedClipboard); else Clipboard.Clear(); return true; }); } catch { }
                }
            }
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

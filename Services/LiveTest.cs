using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SandstormModLauncher.Core;
using SandstormModLauncher.Game;
using SandstormModLauncher.Models;

namespace SandstormModLauncher.Services
{
    /// <summary>
    /// --live-test script.txt out.txt: drives the real game through the launcher's own launch and console code
    /// (screen-verified input) to check game behaviour. Only for when the player says the game is free.
    /// Script lines:
    ///   launch &lt;ScenarioId&gt; [Cls.Key=Value ...] [slots=N] [night]   full launch (writes Game.ini, starts the game if needed)
    ///   console &lt;command | command&gt;                              run console commands, log the answer
    ///   wait &lt;seconds&gt;                                             sleep
    ///   waitround [seconds]                                        wait for the round to be active
    ///   count [label]                                              count players and bots per team
    ///   key &lt;F1|Enter|...&gt;                                         press one key in the game (only when it is in front)
    ///   quit                                                       close the game
    /// </summary>
    public static class LiveTest
    {
        public static async Task Run(string scriptFile, string outFile)
        {
            var log = new StringBuilder();
            void Out(string s)
            {
                string line = DateTime.Now.ToString("HH:mm:ss") + " " + s;
                log.AppendLine(line);
                AppLog.Info("LiveTest: " + s);
                try { File.WriteAllText(outFile, log.ToString()); } catch { }
            }
            var state = new AppState();
            state.Store.Load();
            state.Rules = RulesDb.LoadEmbedded();
            state.Install = GameInstall.Detect(state.Settings.GameDirOverride);
            state.Official = GameCatalog.Load(state.Install, AppPaths.CacheDir, m => { });
            state.Mods = ModScanner.Scan(state.Install, state.Settings.ExtraModFolders, AppPaths.CacheDir, m => { });
            state.Rebuild();
            state.Settings.AutoStartGame = true;
            state.Settings.RestartPolicy = "Always";
            var monitor = new GameMonitor();
            var console = new ConsoleBridge(monitor, () => state.Settings, () => state.Official);
            var launcher = new LaunchService(state, monitor, console);
            string gameIni = GameInstall.GameIniPath;
            string savedIni = File.Exists(gameIni) ? File.ReadAllText(gameIni) : null;
            Out("Game.ini saved for restore (" + (savedIni?.Length ?? 0) + " chars)");
            try
            {
                foreach (var raw in File.ReadAllLines(scriptFile))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int sp = line.IndexOf(' ');
                    string cmd = sp < 0 ? line : line.Substring(0, sp), arg = sp < 0 ? "" : line.Substring(sp + 1).Trim();
                    Out("> " + line);
                    try
                    {
                        switch (cmd.ToLowerInvariant())
                        {
                            case "launch": await Launch(arg, state, launcher, Out); break;
                            case "console":
                            {
                                var res = await console.Run(arg, CancellationToken.None, null, TimeSpan.FromSeconds(3));
                                Out(res.Sent ? (res.NotRecognized ? "  not recognised: " + res.Detail : "  sent") : "  NOT SENT: " + res.Detail);
                                foreach (var l in res.Lines.Where(l => !LogSanitizer.IsSensitive(l)).Select(l => Regex.Replace(l, @"^\[[^\]]*\]\[[^\]]*\]", ""))
                                                            .Where(l => l.Contains(" = ") || l.Contains("not recognized") || l.Contains("LogGameMode") || l.Contains("Bot") || l.Contains("bot")).Take(40))
                                    Out("  | " + LogSanitizer.Clean(l));
                                break;
                            }
                            case "wait": await Task.Delay(TimeSpan.FromSeconds(double.Parse(arg, System.Globalization.CultureInfo.InvariantCulture))); break;
                            case "waitround":
                            {
                                double max = arg.Length > 0 ? double.Parse(arg, System.Globalization.CultureInfo.InvariantCulture) : 60;
                                var until = DateTime.UtcNow.AddSeconds(max);
                                while (DateTime.UtcNow < until && monitor.RoundState != "RoundActive" && monitor.RoundState != "PreRound") { await Task.Delay(500); monitor.Poll(); }
                                Out("  round state: " + (monitor.RoundState ?? "none") + ", phase " + monitor.Phase);
                                break;
                            }
                            case "count": await Count(console, arg, Out); break;
                            case "key":
                            {
                                var input = new GameInput(monitor.Window);
                                ushort vk = arg.Equals("Enter", StringComparison.OrdinalIgnoreCase) ? (ushort)0x0D : input.VirtualKeyFor(arg);
                                if (vk == 0 || !input.Focus(3000)) { Out("  key not sent"); break; }
                                await Task.Delay(500);
                                input.Tap(vk);
                                Out("  pressed " + arg);
                                break;
                            }
                            case "quit": await launcher.StopGame(CancellationToken.None); Out("  game closed"); break;
                            default: Out("  unknown step"); break;
                        }
                    }
                    catch (Exception ex) { Out("  ERROR " + ex.GetType().Name + ": " + ex.Message); }
                    monitor.Poll();
                }
            }
            finally
            {
                try
                {
                    if (System.Diagnostics.Process.GetProcessesByName(GameInstall.ClientProcess).Length == 0 && savedIni != null)
                    {
                        File.WriteAllText(gameIni, savedIni);
                        Out("Game.ini restored");
                    }
                    else Out("Game.ini NOT restored (game still running)");
                }
                catch (Exception ex) { Out("Game.ini restore failed: " + ex.Message); }
                monitor.Dispose();
                Out("done");
            }
        }

        private static async Task Launch(string arg, AppState state, LaunchService launcher, Action<string> Out)
        {
            var parts = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var p = new Profile { Name = "LiveTest", ScenarioId = parts[0], MaxPlayers = 8, Lighting = "Day", MutatorsEnabled = false };
            foreach (var kv in parts.Skip(1))
            {
                if (kv == "night") { p.Lighting = "Night"; continue; }
                if (kv.StartsWith("slots=")) { p.MaxPlayers = int.Parse(kv.Substring(6)); continue; }
                int dot = kv.IndexOf('.'), eq = kv.IndexOf('=');
                if (dot < 0 || eq < dot) continue;
                string cls = kv.Substring(0, dot), key = kv.Substring(dot + 1, eq - dot - 1), val = kv.Substring(eq + 1);
                if (!p.Rules.TryGetValue(cls, out var d)) p.Rules[cls] = d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                d[key] = val;
            }
            var map = state.Maps.FirstOrDefault(m => m.Scenarios.Any(s => s.Id.Equals(p.ScenarioId, StringComparison.OrdinalIgnoreCase)));
            p.MapKey = map?.Key;
            var plan = LaunchPlanner.Build(p, state);
            Out("  plan: " + plan.OpenCommand + (plan.Error != null ? " ERROR " + plan.Error : ""));
            if (plan.GameIniBlock.Length > 0) Out("  ini:\r\n" + plan.GameIniBlock);
            var progress = new Progress<LaunchUpdate>(u => { if (u.State != StepState.Active) Out("  step " + (u.Step + 1) + " " + u.State + ": " + u.Detail); });
            var report = await launcher.Run(plan, new LaunchOptions { RestartIfNeeded = true }, progress, CancellationToken.None);
            Out("  launch " + (report.Success ? "OK" : "FAILED: " + report.Message));
            foreach (var w in report.Warnings) Out("  warning: " + w);
        }

        private static async Task Count(ConsoleBridge console, string label, Action<string> Out)
        {
            var res = await console.Run("getall INSPlayerState TeamId | getall INSPlayerState bIsABot", CancellationToken.None, null, TimeSpan.FromSeconds(3));
            var teams = new Dictionary<string, string>();
            var bots = new Dictionary<string, bool>();
            foreach (var l in res.Lines)
            {
                var t = Regex.Match(l, @"(INSPlayerState_\w+)\.TeamId = (\d+)");
                if (t.Success) teams[t.Groups[1].Value] = t.Groups[2].Value;
                var b = Regex.Match(l, @"(INSPlayerState_\w+)\.bIsABot = (True|False)");
                if (b.Success) bots[b.Groups[1].Value] = b.Groups[2].Value == "True";
            }
            if (!res.Sent) { Out("  count NOT SENT: " + res.Detail); return; }
            var summary = teams.GroupBy(kv => kv.Value).OrderBy(g => g.Key)
                .Select(g => "team " + g.Key + ": " + g.Count(kv => bots.TryGetValue(kv.Key, out var x) && !x) + " human, " + g.Count(kv => bots.TryGetValue(kv.Key, out var x) && x) + " bots");
            Out("  COUNT " + label + ": " + (teams.Count == 0 ? "no player states" : string.Join(" | ", summary)));
        }
    }
}

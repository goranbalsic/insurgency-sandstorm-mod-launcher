using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SandstormModLauncher.Core;
using SandstormModLauncher.Game;
using SandstormModLauncher.Models;

namespace SandstormModLauncher.Services
{
    /// <summary>
    /// Command line for the setup logic, without a window:  SandstormModLauncher.exe [--data dir] --cli [--out file] command args
    /// It works on the active profile of the data folder (use --data for a test copy), prints to the console and to --out.
    ///
    ///   status                                   the setup, its checks and the launch plan in short
    ///   scenarios [text]                         scenario ids (with their mode)
    ///   scenario id | light day|night | hardcore on|off | slots n
    ///   presets squad|styles|official|playlists|saved
    ///   apply squad|styles|official|playlists|saved "name"
    ///   save "name" | delete-saved "name" | reset
    ///   set mode|*|current key value | unset mode|*|current key
    ///   mutators [add id.. | remove id.. | clear]
    ///   plan                                     open command, Game.ini block, after-load commands
    ///   ini-merge in.ini out.ini                 writes the plan into a copy of a Game.ini (read-only files too)
    ///   torture [steps] [seed]                   random stress test of the whole setup logic (checks every step)
    ///   report-send [address]                    sends the cleaned problem report (to the inbox, or to an address for testing)
    ///   server-status | server-plan | server-start | server-stop    the dedicated server on this PC, with the Play match
    ///   server-rcon cmd.. | server-travel | server-players          a running server over its RCON
    ///   server-set key value                     name, port, queryport, maxplayers, password, rconport, rconnetwork, mapcycle,
    ///                                            mapcyclefile, admins, mods, modids, gslt, gamestats, cheats, log, extra, dir,
    ///                                            remote, remotehost, remoteport, remotepassword, useownargs, ownargs,
    ///                                            bat (a .bat file's options), modioemail, modiocode (on/off for switches)
    ///   mapcycle show | add | remove n | clear   the server's map cycle file (add = the Play match)
    ///   translation-template [repo] [out.csv]    every English text, read from the source (Resources\Languages\template.csv)
    ///   translation-check [repo]                 the table the launcher carries has exactly the texts in the source
    /// Exit code 0 = fine, 1 = a check failed or the command was wrong.
    /// </summary>
    public static class Cli
    {
        public static int Run(string[] args, Action<string> print)
        {
            var state = LoadState();
            var p = state.Store.Active;
            string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            string A(int i) => args.Length > i ? args[i] : null;
            bool save = true;
            try
            {
                switch (cmd)
                {
                    case "status": save = false; Status(state, p, print); return Problems(state, p, print);
                    case "scenarios":
                        save = false;
                        foreach (var s in state.AllScenarios.Where(s => A(1) == null || (s.Id + " " + s.GameModeName).IndexOf(A(1), StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(s => s.Id))
                            print(s.Id.PadRight(52) + " " + (state.ModeFor(s, false)?.Cls ?? s.GameModeClass));
                        return 0;
                    case "scenario":
                    {
                        var s = state.AllScenarios.FirstOrDefault(x => x.Id.Equals(A(1) ?? "", StringComparison.OrdinalIgnoreCase));
                        if (s == null) { print("No scenario " + A(1)); return 1; }
                        PickScenario(state, p, s);
                        print("Scenario " + s.Id + " (" + SetupEngine.CurrentMode(p, state)?.Cls + ")");
                        return 0;
                    }
                    case "light": p.Lighting = (A(1) ?? "").Equals("night", StringComparison.OrdinalIgnoreCase) ? "Night" : "Day"; print("Lighting " + p.Lighting); return 0;
                    case "hardcore": p.Hardcore = (A(1) ?? "").Equals("on", StringComparison.OrdinalIgnoreCase); print("Hardcore " + p.Hardcore); return 0;
                    case "slots":
                        if (!int.TryParse(A(1) ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out int slots)) { print("slots needs a number"); save = false; return 1; }
                        p.MaxPlayers = Math.Max(1, Math.Min(64, slots)); print("Player slots " + p.MaxPlayers); return 0;
                    case "presets":
                        save = false;
                        foreach (var pr in Presets(state, p, A(1) ?? "styles")) print(pr.Name.PadRight(40) + " " + pr.Tag.PadRight(7) + " " + Trim(pr.Description, 80));
                        return 0;
                    case "apply":
                    {
                        var pr = Presets(state, p, A(1) ?? "").FirstOrDefault(x => x.Name.Equals(A(2) ?? "", StringComparison.OrdinalIgnoreCase));
                        if (pr == null) { print("No preset \"" + A(2) + "\" in " + A(1)); return 1; }
                        print(SetupEngine.Apply(p, state, pr));
                        return Problems(state, p, print);
                    }
                    case "save":
                        state.Settings.RulesPresets.RemoveAll(r => r.Name.Equals(A(1) ?? "", StringComparison.OrdinalIgnoreCase));
                        state.Settings.RulesPresets.Add(SetupEngine.Capture(p, A(1) ?? "Saved setup"));
                        // As the Save button does: the setup on screen is now that saved setup.
                        SetupEngine.MarkSetup(p, A(1) ?? "Saved setup");
                        print("Saved \"" + A(1) + "\"");
                        return 0;
                    case "delete-saved":
                        print(state.Settings.RulesPresets.RemoveAll(r => r.Name.Equals(A(1) ?? "", StringComparison.OrdinalIgnoreCase)) > 0 ? "Deleted" : "Not found");
                        return 0;
                    case "reset": SetupEngine.ResetAll(p); print("Rules and mutators reset"); return 0;
                    case "set":
                    case "unset":
                    {
                        string cls = A(1) == "current" ? SetupEngine.CurrentMode(p, state)?.Cls : A(1);
                        SetupEngine.SetRule(p, state.Rules, cls, A(2), cmd == "set" ? A(3) : null);
                        print(cls + "." + A(2) + " = " + (SetupEngine.Effective(p, state.Rules, cls, A(2)) ?? "(none)"));
                        return Problems(state, p, print);
                    }
                    case "mutators":
                    {
                        var list = new List<string>(p.Mutators);
                        if (A(1) == "clear") list.Clear();
                        else if (A(1) == "add") list.AddRange(args.Skip(2));
                        else if (A(1) == "remove") list.RemoveAll(m => args.Skip(2).Contains(m, StringComparer.OrdinalIgnoreCase));
                        else save = false;
                        var missing = new List<string>();
                        if (save) SetupEngine.SetMutators(p, state, list, missing);
                        print("Mutators: " + (p.Mutators.Count == 0 ? "none" : string.Join(", ", p.Mutators)) + (missing.Count > 0 ? "   not installed: " + string.Join(", ", missing) : ""));
                        return 0;
                    }
                    case "plan":
                    {
                        save = false;
                        var plan = LaunchPlanner.Build(p, state);
                        print("Open: " + plan.OpenCommand);
                        print("Slots: " + plan.PlayerSlots + (plan.Error != null ? "   ERROR " + plan.Error : ""));
                        foreach (var w in plan.Warnings) print("Warning: " + w);
                        print("After load: " + string.Join(" | ", plan.AfterLoad));
                        print(plan.GameIniBlock);
                        return plan.Error == null ? 0 : 1;
                    }
                    case "ini-merge":
                    {
                        save = false;
                        var plan = LaunchPlanner.Build(p, state);
                        if (File.Exists(A(2))) new FileInfo(A(2)).IsReadOnly = false;
                        File.Copy(A(1), A(2), true);
                        new FileInfo(A(2)).IsReadOnly = new FileInfo(A(1)).IsReadOnly;
                        UeIni.WriteText(A(2), LaunchPlanner.GameIniForLaunch(UeIni.ReadText(A(2)), plan, state.Rules, state.Settings.ManagedIniKeys, state.Settings));
                        print("Merged into " + A(2) + " (read-only afterwards: " + new FileInfo(A(2)).IsReadOnly + ")");
                        return 0;
                    }
                    case "report-send":
                    {
                        // report-send [address]: builds the cleaned report and sends it (to the inbox, or to the address given, for testing).
                        save = false;
                        DebugReport.BuildPublic("CLI test report", state, null, out string shortText, out string fullText);
                        print("Sent, id " + DebugReport.Send("CLI test report", shortText, fullText, A(1)));
                        return 0;
                    }
                    case "ini-write":
                    {
                        // ini-write file: merges the plan into that file in place, the same way a launch writes Game.ini.
                        save = false;
                        var plan = LaunchPlanner.Build(p, state);
                        UeIni.WriteText(A(1), LaunchPlanner.GameIniForLaunch(UeIni.ReadText(A(1)), plan, state.Rules, state.Settings.ManagedIniKeys, state.Settings));
                        print("Written: " + A(1) + " (read-only: " + new FileInfo(A(1)).IsReadOnly + ")");
                        return 0;
                    }
                    case "rcon":
                    {
                        // rcon <command> [command ...]: to the running game over RCON (quote a console line: "getall X Y").
                        save = false;
                        var rcon = new GameRcon(() => state.Settings);
                        var replies = rcon.Run(args.Skip(1).ToArray());
                        for (int i = 0; i < replies.Count; i++) { print(">> " + args[i + 1]); print(replies[i].TrimEnd()); }
                        return 0;
                    }
                    case "server-status":
                    {
                        save = false;
                        var inst = ServerInstall.Detect(state.Settings.ServerDirOverride);
                        var svc = new ServerService(() => state.Settings, () => inst);
                        print("Server folder: " + (inst.Root ?? "not found") + (inst.BuildId.Length > 0 ? " (build " + inst.BuildId + ")" : ""));
                        print("Running here: " + svc.IsRunning);
                        string problem = svc.Rcon.Probe();
                        print("RCON " + svc.Rcon.Host + ": " + (problem == null ? "connected" : "not reachable (" + problem + ")"));
                        return 0;
                    }
                    case "server-plan":
                    {
                        save = false;
                        var inst = ServerInstall.Detect(state.Settings.ServerDirOverride);
                        var plan = ServerPlanFor(state, p, inst);
                        PrintServerPlan(plan, print);
                        return plan.IsValid ? 0 : 1;
                    }
                    case "server-start":
                    {
                        var inst = ServerInstall.Detect(state.Settings.ServerDirOverride);
                        var plan = ServerPlanFor(state, p, inst);
                        PrintServerPlan(plan, print);
                        if (!plan.IsValid) return 1;
                        var svc = new ServerService(() => state.Settings, () => inst);
                        svc.Watch();
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        string failed = System.Threading.Tasks.Task.Run(() => svc.Start(plan, new Progress<string>(t => print("  " + t)), System.Threading.CancellationToken.None)).GetAwaiter().GetResult();
                        print(failed == null ? "Server running after " + (int)sw.Elapsed.TotalSeconds + " s" : "NOT STARTED: " + failed);
                        svc.Monitor?.Dispose();
                        return failed == null ? 0 : 1;
                    }
                    case "server-install":
                    {
                        // server-install [folder] [validate] [stop-after=seconds]: installs (or updates) the dedicated server with SteamCMD,
                        // as the Server page does; stop-after presses Stop after that many seconds (the page's Stop).
                        string dir = args.Length > 1 && args[1] != "validate" && !args[1].StartsWith("stop-after=") ? args[1] : !string.IsNullOrWhiteSpace(state.Settings.ServerInstallDir) ? state.Settings.ServerInstallDir : null;
                        if (dir == null) { print("server-install <folder> [validate]"); save = false; return 1; }
                        bool validate = args.Contains("validate");
                        string lastStage = null; int lastPct = -1;
                        // Reported at once (a Progress<T> would wait for the blocked main thread).
                        var progress = new DirectProgress<SteamCmdProgress>(u =>
                        {
                            int pct = u.Percent.HasValue ? (int)u.Percent.Value : -1;
                            if (u.Error != null) print("  error: " + u.Error);
                            else if (u.Stage != lastStage || pct / 10 != lastPct / 10) print("  " + u.Stage + (pct >= 0 ? " " + pct + "%" : "") + (u.Total > 0 ? " (" + u.Done + " / " + u.Total + ")" : ""));
                            lastStage = u.Stage; lastPct = pct;
                        });
                        var cts = new System.Threading.CancellationTokenSource();
                        string stopArg = args.FirstOrDefault(x => x.StartsWith("stop-after="));
                        if (stopArg != null && int.TryParse(stopArg.Substring(11), out int stopSecs)) cts.CancelAfter(TimeSpan.FromSeconds(stopSecs));
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var r = System.Threading.Tasks.Task.Run(() => SteamCmd.InstallServer(dir, validate, progress, cts.Token)).GetAwaiter().GetResult();
                        print((r.Ok ? (r.UpToDate ? "Up to date: " : "Installed: ") + dir : r.Cancelled ? "STOPPED: " + r.Error : "NOT INSTALLED: " + r.Error) + " after " + (int)sw.Elapsed.TotalSeconds + " s; SteamCMD still running: " + SteamCmd.IsRunning);
                        if (r.Ok) state.Settings.ServerDirOverride = dir;
                        return r.Ok ? 0 : 1;
                    }
                    case "server-type":
                    {
                        // server-type <coop|coop-hardcore|coop-frenzy|push|competitive> [night]: sets the server up as that kind, as the Server page does.
                        var type = ServerTypes.Find(A(1) ?? "");
                        if (type == null) { print("server-type " + string.Join("|", ServerTypes.All.Select(t => t.Id)) + " [night]"); save = false; return 1; }
                        string text = ServerTypes.Apply(p, state, type);
                        if (text == null) { print("No scenario of that mode"); save = false; return 1; }
                        var inst = ServerInstall.Detect(state.Settings.ServerDirOverride);
                        string file = ServerPlanner.MapCyclePath(state.Settings, inst);
                        var cycle = ServerTypes.Cycle(state, type, args.Contains("night"));
                        if (file != null)
                        {
                            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
                            if (System.IO.File.Exists(file)) ConsoleBridge.BackupFile(file, "server-");
                            System.IO.File.WriteAllText(file, MapCycle.Render(cycle), new System.Text.UTF8Encoding(false));
                            state.Settings.ServerUseMapCycle = true;
                        }
                        state.Settings.ServerMaxPlayers = type.MaxPlayers;
                        print(text + " · " + cycle.Count + " scenarios in the map cycle" + (file == null ? " (not written: no server folder)" : " (" + file + ")"));
                        return 0;
                    }
                    case "server-stop":
                    {
                        save = false;
                        var inst = ServerInstall.Detect(state.Settings.ServerDirOverride);
                        var svc = new ServerService(() => state.Settings, () => inst);
                        bool stopped = System.Threading.Tasks.Task.Run(() => svc.Stop(System.Threading.CancellationToken.None)).GetAwaiter().GetResult();
                        print(stopped ? "Server stopped" : "The server did not close");
                        return stopped ? 0 : 1;
                    }
                    case "server-rcon":
                    {
                        // server-rcon <command> [command ...]: to the dedicated server over its RCON.
                        save = false;
                        var inst = ServerInstall.Detect(state.Settings.ServerDirOverride);
                        var svc = new ServerService(() => state.Settings, () => inst);
                        var replies = svc.Rcon.Run(args.Skip(1).ToArray());
                        for (int i = 0; i < replies.Count; i++) { print(">> " + args[i + 1]); print(replies[i].TrimEnd()); }
                        return 0;
                    }
                    case "server-travel":
                    {
                        // Loads the Play match on the running server.
                        save = false;
                        var inst = ServerInstall.Detect(state.Settings.ServerDirOverride);
                        var plan = ServerPlanFor(state, p, inst);
                        if (!plan.IsValid) { print(plan.Error); return 1; }
                        var r = new ServerService(() => state.Settings, () => inst).Travel(plan);
                        print(r.Ok ? r.Text : "NOT TRAVELLING: " + (r.Error ?? r.Text));
                        return r.Ok || r.Delivered ? 0 : 1;
                    }
                    case "server-players":
                    {
                        save = false;
                        var inst = ServerInstall.Detect(state.Settings.ServerDirOverride);
                        var players = new ServerService(() => state.Settings, () => inst).Players();
                        print(players.Count + " player(s)");
                        // Names and ids are the players' own: only the count and the columns that are not personal are printed.
                        foreach (var pl in players) print("  id " + pl.Id + "  score " + pl.Score);
                        return 0;
                    }
                    case "server-set":
                    {
                        string key = (A(1) ?? "").ToLowerInvariant(), value = A(2) ?? "";
                        if (!SetServerOption(state.Settings, key, value)) { print("server-set " + key + ": unknown key or bad value"); save = false; return 1; }
                        print("Server " + key + " set");
                        return 0;
                    }
                    case "mapcycle":
                    {
                        // mapcycle show | add | remove <n> | clear: the server's map cycle file (add = the Play match).
                        var inst = ServerInstall.Detect(state.Settings.ServerDirOverride);
                        string file = ServerPlanner.MapCyclePath(state.Settings, inst);
                        if (file == null) { print("No map cycle file (set the server folder or server-set mapcyclefile <path>)"); save = false; return 1; }
                        var entries = File.Exists(file) ? MapCycle.Parse(File.ReadAllText(file)) : new List<MapCycleEntry>();
                        string sub = (A(1) ?? "show").ToLowerInvariant();
                        if (sub == "add")
                        {
                            var plan = LaunchPlanner.Build(p, state);
                            if (!plan.IsValid) { print(plan.Error); return 1; }
                            entries.Add(MapCycle.For(plan));
                        }
                        else if (sub == "remove" && int.TryParse(A(2) ?? "", out int at) && at >= 1 && at <= entries.Count) entries.RemoveAt(at - 1);
                        else if (sub == "clear") entries.Clear();
                        else if (sub != "show") { print("mapcycle show | add | remove <n> | clear"); save = false; return 1; }
                        if (sub != "show")
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(file));
                            if (File.Exists(file)) ConsoleBridge.BackupFile(file, "server-");
                            File.WriteAllText(file, MapCycle.Render(entries), new UTF8Encoding(false));
                        }
                        print(file);
                        for (int i = 0; i < entries.Count; i++) print("  " + (i + 1) + ". " + entries[i].Line);
                        save = false;
                        return 0;
                    }
                    case "translation-template":
                        // translation-template [repo folder] [out.csv]: every English text of the launcher, from its source
                        save = false;
                        return TranslationTemplate.Write(A(1) ?? ".", A(2) ?? Path.Combine(A(1) ?? ".", "Resources", "Languages", "template.csv"), print);
                    case "assets":
                    {
                        // assets <text>: game assets whose path or class contains the text (to check the rules database against a game update)
                        save = false;
                        string filter = A(1) ?? "";
                        var paks = Directory.GetFiles(state.Install.PaksDir, "*.pak").Select(PakFile.Open).ToList();
                        byte[] ar = null;
                        foreach (var pk in paks) if (pk.TryRead("Insurgency/AssetRegistry.bin", out ar)) break;
                        if (ar == null) { print("No asset registry found"); return 1; }
                        foreach (var a in AssetRegistry.Parse(ar).Where(a => (a.ObjectPath + " " + a.AssetClass).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(a => a.ObjectPath))
                            print(a.AssetClass + "  " + a.ObjectPath + (a.Tags.Count > 0 ? "  [" + string.Join("; ", a.Tags.Select(t => t.Key + "=" + (t.Value.Length > 80 ? t.Value.Substring(0, 80) + "..." : t.Value))) + "]" : ""));
                        return 0;
                    }
                    case "translation-check":
                    {
                        // translation-check [repo folder]: fails when texts were changed without making the table again
                        save = false;
                        var source = TranslationTemplate.Scan(A(1) ?? ".").Select(r => r.English).ToList();
                        var carried = Loc.Template().Select(r => r.English).ToList();
                        var missing = source.Except(carried).ToList();
                        var extra = carried.Except(source).ToList();
                        foreach (var t in missing.Take(20)) print("not in the table: " + t.Replace("\n", "\\n"));
                        foreach (var t in extra.Take(20)) print("no longer in the source: " + t.Replace("\n", "\\n"));
                        print(source.Count + " texts in the source, " + carried.Count + " in the table: " + (missing.Count + extra.Count == 0 ? "the same" : missing.Count + " missing, " + extra.Count + " extra"));
                        return missing.Count + extra.Count == 0 ? 0 : 1;
                    }
                    case "rcon-status":
                    {
                        save = false;
                        string problem = new GameRcon(() => state.Settings).Probe();
                        print("RCON " + RconSetup.Address + ":" + state.Settings.RconPort + ": " + (problem == null ? "connected" : "not reachable (" + problem + ")"));
                        print("Game.ini has the launcher's RCON section: " + RconSetup.GameIniHasIt(state.Settings));
                        return problem == null ? 0 : 1;
                    }
                    case "rcon-torture":
                        save = false;
                        if (!int.TryParse(A(1) ?? "400", NumberStyles.Integer, CultureInfo.InvariantCulture, out int rsteps) || !int.TryParse(A(2) ?? "1", NumberStyles.Integer, CultureInfo.InvariantCulture, out int rseed))
                        { print("rcon-torture [steps] [seed] takes numbers"); return 1; }
                        return RconTorture.Run(rsteps, rseed, print);
                    case "torture":
                        save = false;
                        if (!int.TryParse(A(1) ?? "2000", NumberStyles.Integer, CultureInfo.InvariantCulture, out int steps) || !int.TryParse(A(2) ?? "1", NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed))
                        { print("torture [steps] [seed] takes numbers"); return 1; }
                        return Torture.Run(state, steps, seed, print);
                    default:
                        save = false;
                        print("Unknown command " + cmd + ". See Services/Cli.cs for the list.");
                        return 1;
                }
            }
            finally
            {
                if (save) { state.Store.SaveProfile(p); state.Store.SaveSettings(); }
            }
        }

        public static AppState LoadState()
        {
            var state = new AppState();
            state.Store.Load();
            state.Rules = RulesDb.LoadEmbedded();
            SetupEngine.CleanStored(state);
            SetupEngine.MigrateToSetups(state);
            state.Install = GameInstall.Detect(state.Settings.GameDirOverride);
            string cache = GameInstall.DemoCacheDir ?? AppPaths.CacheDir;
            state.Official = GameCatalog.Load(state.Install, cache, m => { });
            state.Mods = ModScanner.Scan(state.Install, state.Settings.ExtraModFolders, cache, m => { });
            state.Rebuild();
            return state;
        }

        public static void PickScenario(AppState state, Profile p, ScenarioInfo s)
        {
            p.ScenarioId = s.Id;
            p.CustomMapId = null;
            p.MapKey = state.Maps.FirstOrDefault(m => m.Scenarios.Any(x => x.Id == s.Id))?.Key ?? p.MapKey;
            if (s.GameModeClass != "INSCheckpointGameMode") p.Hardcore = false;
        }

        public static List<Preset> Presets(AppState state, Profile p, string kind)
        {
            bool coop = SetupEngine.CurrentMode(p, state)?.Coop ?? true;
            switch ((kind ?? "").ToLowerInvariant())
            {
                case "squad": return SetupEngine.SquadPresets(state.Rules, coop);
                case "official": return SetupEngine.OfficialPresets(state.Rules);
                case "playlists": case "playlist": return SetupEngine.PlaylistPresets(state, coop);
                case "saved": return SetupEngine.SavedPresets(state);
                default: return SetupEngine.StylePresets(state.Rules);
            }
        }

        private static void Status(AppState state, Profile p, Action<string> print)
        {
            var mode = SetupEngine.CurrentMode(p, state);
            print("Profile " + p.Name + ": " + p.ScenarioId + " (" + (mode?.Cls ?? "no mode") + "), " + p.Lighting + (p.Hardcore ? ", hardcore" : "") + ", " + p.MaxPlayers + " slots");
            print("Preset: " + (p.RulesPresetName == null ? "none" : SetupEngine.PresetLabel(p)));
            foreach (var mr in p.Rules.OrderBy(k => k.Key))
                print("  " + mr.Key + ": " + string.Join(", ", mr.Value.Select(kv => kv.Key + "=" + kv.Value)));
            print("Mutators (" + (p.MutatorsEnabled ? "on" : "off") + "): " + (p.Mutators.Count == 0 ? "none" : string.Join(", ", p.Mutators)));
            var plan = LaunchPlanner.Build(p, state);
            print("Open: " + plan.OpenCommand + (plan.Error != null ? "   ERROR " + plan.Error : ""));
        }

        private static int Problems(AppState state, Profile p, Action<string> print)
        {
            var problems = SetupEngine.Problems(p, state);
            foreach (var x in problems) print("PROBLEM: " + x);
            return problems.Count == 0 ? 0 : 1;
        }

        private static string Trim(string s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s.Substring(0, n) + "...";

        private static ServerPlan ServerPlanFor(AppState state, Profile p, ServerInstall inst)
        {
            string ini = inst.Found && File.Exists(inst.GameIniPath) ? UeIni.ReadText(inst.GameIniPath) : "";
            return ServerPlanner.Build(LaunchPlanner.Build(p, state), state.Settings, inst, state.Rules, ini, ServerModio.ReadAccount());
        }

        private static void PrintServerPlan(ServerPlan plan, Action<string> print)
        {
            if (!plan.IsValid) { print("PROBLEM: " + plan.Error); return; }
            print("Command line: " + plan.ShownCommandLine);
            if (plan.MatchModIds.Count > 0) print("Mods the match needs: " + string.Join(", ", plan.MatchModIds));
            if (plan.AdminsText != null) print("Admins.txt: " + UeIni.Split(plan.AdminsText).Count(l => l.Trim().Length > 0) + " admin(s)");
            if (plan.ModsOn)
            {
                var acc = ServerModio.ReadAccount();
                print("mod.io login: " + (acc.LoggedIn ? "yes, " + acc.Subscriptions.Count + " subscription(s)" + (acc.SameAsGame ? ", SAME ACCOUNT AS THE GAME" : "") : acc.Expired ? "expired" : "no")
                      + (plan.UsesCode ? " (a security code is passed)" : ""));
            }
            if (plan.MapCycleName != null) print("Map cycle: " + plan.MapCycleName + (plan.MapCycleCopyFrom != null ? " (copied from " + plan.MapCycleCopyFrom + ")" : ""));
            print("Game.ini:");
            foreach (var l in UeIni.Split(DebugReport.HidePasswords(plan.GameIni))) print("  " + l);
            foreach (var w in plan.Warnings) print("warning: " + w);
        }

        /// <summary>server-set: one server setting from the command line (on/off for switches).</summary>
        private static bool SetServerOption(AppSettings s, string key, string value)
        {
            bool? on = value.Equals("on", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase) ? true
                     : value.Equals("off", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase) ? false : (bool?)null;
            bool num = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n);
            switch (key)
            {
                case "name": s.ServerName = value; return true;
                case "password": s.ServerPassword = value; return true;
                case "port": if (!num) return false; s.ServerPort = n; return true;
                case "queryport": if (!num) return false; s.ServerQueryPort = n; return true;
                case "maxplayers": if (!num) return false; s.ServerMaxPlayers = n; return true;
                case "rconport": if (!num) return false; s.ServerRconPort = n; return true;
                case "rconnetwork": if (on == null) return false; s.ServerRconFromNetwork = on.Value; return true;
                case "mapcycle": if (on == null) return false; s.ServerUseMapCycle = on.Value; return true;
                case "mapcyclefile": s.ServerMapCycleFile = value; return true;
                case "admins": s.ServerAdmins = value.Replace(",", "\n"); return true;
                case "mods": if (on == null) return false; s.ServerModsEnabled = on.Value; return true;
                case "modids": s.ServerMods = value.Replace(",", "\n"); return true;
                case "gslt": s.ServerGslt = value; return true;
                case "gamestats": if (on == null) return false; s.ServerGameStats = on.Value; return true;
                case "gamestatstoken": s.ServerGameStatsToken = value; return true;
                case "cheats": if (on == null) return false; s.ServerCheats = on.Value; return true;
                case "log": if (on == null) return false; s.ServerShowLog = on.Value; return true;
                case "extra": s.ServerExtraArgs = value; return true;
                case "dir": s.ServerDirOverride = value; return true;
                case "remote": if (on == null) return false; s.ServerRemote = on.Value; return true;
                case "remotehost": s.ServerRemoteHost = value; return true;
                case "remoteport": if (!num) return false; s.ServerRemoteRconPort = n; return true;
                case "remotepassword": s.ServerRemoteRconPassword = value; return true;
                case "votekick": if (on == null) return false; s.ServerVoteKick = on.Value; return true;
                case "officialrules": if (on == null) return false; s.ServerOfficialRules = on.Value; return true;
                case "ownrules": if (on == null) return false; s.ServerOwnRules = on.Value; return true;
                case "installdir": s.ServerInstallDir = value; return true;
                case "useownargs": if (on == null) return false; s.ServerUseOwnArgs = on.Value; return true;
                case "ownargs": s.ServerOwnArgs = value; return true;
                case "bat":
                    if (!File.Exists(value)) return false;
                    string args = ServerArgs.FromBatch(File.ReadAllText(value));
                    if (string.IsNullOrWhiteSpace(args)) return false;
                    s.ServerOwnArgs = args;
                    s.ServerUseOwnArgs = true;
                    return true;
                case "modioemail": s.ServerModioEmail = value; return true;
                case "modiocode": s.ServerModioCode = value; return true;
                default: return false;
            }
        }
    }

    /// <summary>A progress report handled at once, on the thread that reports it (for command-line tools).</summary>
    public sealed class DirectProgress<T> : IProgress<T>
    {
        private readonly Action<T> handler;
        private readonly object gate = new object();
        public DirectProgress(Action<T> handler) { this.handler = handler; }
        public void Report(T value) { lock (gate) handler(value); }
    }
}

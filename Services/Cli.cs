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
                        SetupEngine.MarkPreset(p, A(1), true);
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
    }
}

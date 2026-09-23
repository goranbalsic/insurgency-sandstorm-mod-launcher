using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SandstormModLauncher.Core
{
    public sealed class ExecParam
    {
        public string Name { get; set; }
        public string Type { get; set; }
    }

    public sealed class ExecCommand
    {
        public string Name { get; set; }
        public List<ExecParam> Params { get; set; } = new List<ExecParam>();
        public uint Flags { get; set; }

        [JsonIgnore] public string Signature => Name + (Params.Count == 0 ? "" : " " + string.Join(" ", Params.Select(p => "<" + p.Name + ">")));
        [JsonIgnore] public string ParamText => string.Join("  ", Params.Select(p => p.Name + ": " + p.Type));
        [JsonIgnore] public string Note => ExecCatalog.NoteFor(Name);
    }

    /// <summary>
    /// Lists the console commands (exec functions) compiled into the game executable. Unreal keeps a
    /// FFunctionParams record for every reflected function: name, parameter list and function flags.
    /// The ones flagged FUNC_Exec are what the console accepts.
    /// </summary>
    public static class ExecCatalog
    {
        private const uint FuncExec = 0x200;
        private const ulong CpfParm = 0x80, CpfReturnParm = 0x400;

        private static readonly string[] Types =
        {
            "byte", "int8", "int16", "int", "int64", "uint16", "uint32", "uint64", "int", "uint", "float", "double",
            "bool", "class", "object", "object", "object", "class", "object", "interface", "name", "string",
            "array", "map", "set", "struct", "delegate", "delegate", "delegate", "text", "enum", "field"
        };

        private sealed class Section
        {
            public string Name;
            public ulong Va;
            public uint VirtualSize, RawPtr, RawSize;
            public bool Code;
        }

        public sealed class Cache
        {
            public string Key { get; set; }
            public List<ExecCommand> Commands { get; set; } = new List<ExecCommand>();
        }

        /// <summary>Cached scan of the installed game's executable (rescanned when the exe changes).</summary>
        public static List<ExecCommand> Load(string exePath, string cacheDir)
        {
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return new List<ExecCommand>();
            var fi = new FileInfo(exePath);
            string key = fi.Length + "|" + fi.LastWriteTimeUtc.Ticks + "|2";
            string cacheFile = Path.Combine(cacheDir, "commands.json");
            try
            {
                if (File.Exists(cacheFile))
                {
                    var c = Json.Deserialize<Cache>(File.ReadAllText(cacheFile));
                    if (c != null && c.Key == key && c.Commands.Count > 0) return c.Commands;
                }
            }
            catch { }
            var list = Scan(exePath);
            try
            {
                Directory.CreateDirectory(cacheDir);
                File.WriteAllText(cacheFile, Json.Serialize(new Cache { Key = key, Commands = list }), new UTF8Encoding(false));
            }
            catch (Exception ex) { AppLog.Warn("Command cache: " + ex.Message); }
            return list;
        }

        public static List<ExecCommand> Scan(string exePath)
        {
            byte[] d = File.ReadAllBytes(exePath);
            int pe = BitConverter.ToInt32(d, 0x3C);
            if (BitConverter.ToUInt32(d, pe) != 0x00004550) throw new InvalidDataException("Not a PE file");
            int count = BitConverter.ToUInt16(d, pe + 6);
            int optSize = BitConverter.ToUInt16(d, pe + 20);
            int opt = pe + 24;
            ulong imageBase = BitConverter.ToUInt64(d, opt + 24);
            var sections = new List<Section>();
            for (int i = 0; i < count; i++)
            {
                int o = opt + optSize + i * 40;
                sections.Add(new Section
                {
                    Name = Encoding.ASCII.GetString(d, o, 8).TrimEnd('\0'),
                    VirtualSize = BitConverter.ToUInt32(d, o + 8),
                    Va = imageBase + BitConverter.ToUInt32(d, o + 12),
                    RawSize = BitConverter.ToUInt32(d, o + 16),
                    RawPtr = BitConverter.ToUInt32(d, o + 20),
                    Code = (BitConverter.ToUInt32(d, o + 36) & 0x20000000) != 0
                });
            }
            var dataSections = sections.Where(s => s.Name == ".rdata" || s.Name == ".data").ToList();

            Section SectionOf(ulong v) => sections.FirstOrDefault(s => v >= s.Va && v < s.Va + Math.Max(s.VirtualSize, s.RawSize));
            long Off(ulong v)
            {
                var s = SectionOf(v);
                if (s == null || v - s.Va >= s.RawSize) return -1;
                return s.RawPtr + (long)(v - s.Va);
            }
            bool IsCode(ulong v) { var s = SectionOf(v); return s != null && s.Code; }

            // Identifier-like C strings in the data sections: candidate function and parameter names.
            var names = new Dictionary<ulong, string>();
            foreach (var s in dataSections)
            {
                long start = s.RawPtr, end = Math.Min(d.Length, (long)s.RawPtr + s.RawSize);
                for (long i = start + 1; i < end; i++)
                {
                    if (d[i - 1] != 0 || !IdStart(d[i])) continue;
                    long j = i + 1;
                    while (j < end && IdChar(d[j]) && j - i <= 80) j++;
                    if (j < end && d[j] == 0 && j - i >= 3 && j - i <= 80)
                        names[s.Va + (ulong)(i - s.RawPtr)] = Encoding.ASCII.GetString(d, (int)i, (int)(j - i));
                    i = j;
                }
            }

            string CStr(ulong v)
            {
                long o = Off(v);
                if (o < 0) return null;
                long e = o;
                while (e < d.Length && d[e] != 0 && e - o < 128) e++;
                if (e >= d.Length || d[e] != 0) return null;
                return Encoding.UTF8.GetString(d, (int)o, (int)(e - o));
            }

            var found = new Dictionary<string, ExecCommand>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in dataSections)
            {
                long start = s.RawPtr, end = Math.Min(d.Length - 64, (long)s.RawPtr + s.RawSize - 8);
                for (long p = start + 16; p < end; p += 8)
                {
                    ulong nameVa = BitConverter.ToUInt64(d, (int)p);
                    if (!names.TryGetValue(nameVa, out string fname)) continue;
                    // FFunctionParams: OuterFunc, SuperFunc, NameUTF8, OwningClassName, DelegateName, StructureSize,
                    // PropertyArray, NumProperties, ObjectFlags, FunctionFlags
                    ulong outer = BitConverter.ToUInt64(d, (int)p - 16), super = BitConverter.ToUInt64(d, (int)p - 8);
                    if (!IsCode(outer) || (super != 0 && !IsCode(super))) continue;
                    ulong owning = BitConverter.ToUInt64(d, (int)p + 8), deleg = BitConverter.ToUInt64(d, (int)p + 16);
                    ulong size = BitConverter.ToUInt64(d, (int)p + 24), props = BitConverter.ToUInt64(d, (int)p + 32);
                    if ((owning != 0 && !names.ContainsKey(owning)) || (deleg != 0 && !names.ContainsKey(deleg)) || size > 0x10000) continue;
                    int numProps = BitConverter.ToInt32(d, (int)p + 40);
                    uint funcFlags = BitConverter.ToUInt32(d, (int)p + 48);
                    if (numProps < 0 || numProps > 64 || (numProps > 0 && props == 0)) continue;
                    if ((funcFlags & FuncExec) == 0 || found.ContainsKey(fname)) continue;

                    var cmd = new ExecCommand { Name = fname, Flags = funcFlags };
                    bool ok = true;
                    for (int k = 0; k < numProps && ok; k++)
                    {
                        long po = Off(props + (ulong)(k * 8));
                        if (po < 0) { ok = false; break; }
                        long ppo = Off(BitConverter.ToUInt64(d, (int)po));
                        if (ppo < 0) { ok = false; break; }
                        string pname = CStr(BitConverter.ToUInt64(d, (int)ppo));
                        ulong pflags = BitConverter.ToUInt64(d, (int)ppo + 16);
                        int ptype = d[ppo + 24] & 0x1F;
                        if (pname == null) { ok = false; break; }
                        if ((pflags & CpfParm) != 0 && (pflags & CpfReturnParm) == 0)
                            cmd.Params.Add(new ExecParam { Name = pname, Type = ptype < Types.Length ? Types[ptype] : "?" });
                    }
                    if (ok) found[fname] = cmd;
                }
            }
            return found.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool IdStart(byte b) => (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') || b == '_';
        private static bool IdChar(byte b) => IdStart(b) || (b >= '0' && b <= '9');

        /// <summary>Plain-language notes for the commands that matter for local play.</summary>
        private static readonly Dictionary<string, string> Notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["EnableCheats"] = "Allows cheat commands for this match.",
            ["AdminRestartRound"] = "Restarts the round. 1 switches the teams.",
            ["AdminExtendRoundTimer"] = "Adds a fixed block of time to the round clock.",
            ["SetRoundTimer"] = "Sets the time left in the round, in seconds.",
            ["IgnoreRoundOver"] = "1 keeps the round going forever, 0 turns it back off.",
            ["AdminForceGameOver"] = "Ends the match.",
            ["AdminSetGamemodeProperty"] = "Changes a game mode setting for this match.",
            ["AdminGetGamemodeProperty"] = "Prints the current value of a game mode setting.",
            ["AdminTravelScenario"] = "Switches to another scenario.",
            ["AdminTravelMap"] = "Travels to a map with URL options.",
            ["AdminRespawnAllPlayers"] = "Respawns everybody.",
            ["AdminKickPlayer"] = "Kicks a player by id.",
            ["AIDifficulty"] = "Bot skill from 0 (easiest) to 1 (hardest).",
            ["AISetBotsAmount"] = "Changes the number of bots.",
            ["AISpawn"] = "Spawns a bot for a team.",
            ["AIPurge"] = "Removes all bots.",
            ["AIPurgeEnemy"] = "Removes the enemy bots.",
            ["AIPurgeFriendly"] = "Removes the bots on your team.",
            ["AIRespawnBots"] = "Respawns the bots.",
            ["RespawnAllBots"] = "Respawns every bot.",
            ["AIRespawnEnemyBots"] = "Respawns the enemy bots.",
            ["AIRespawnFriendlyBots"] = "Respawns the bots on your team.",
            ["AIToggle"] = "Freezes or unfreezes all bots.",
            ["AIIgnorePlayers"] = "Bots ignore all players.",
            ["AINoTargetPlayer"] = "Bots ignore you.",
            ["AIModifyMorale"] = "Changes bot morale.",
            ["InstaCap"] = "Captures the objective you are on.",
            ["CheatCaptureObjective"] = "Captures the current objective.",
            ["InstantlyCaptureObjective"] = "Captures an objective for a team.",
            ["CheatCounterAttack"] = "Starts a counter-attack now.",
            ["CheatFinishCounterAttack"] = "Ends the running counter-attack.",
            ["SkipToExtraction"] = "Jumps to the extraction finale.",
            ["TeleportToObjective"] = "Teleports you to an objective.",
            ["KillAllPlayersForTeam"] = "Kills everyone on a team.",
            ["God"] = "You can't be hurt. Run again to turn off.",
            ["GodMode"] = "You can't be hurt. Run again to turn off.",
            ["GodModeAllPlayer"] = "Nobody on your side can be hurt.",
            ["Ghost"] = "Fly through walls.",
            ["Fly"] = "Fly with collision.",
            ["Noclip"] = "Fly through walls.",
            ["Walk"] = "Back to normal movement after Fly or Ghost.",
            ["ResupplyNow"] = "Refills ammo and gear.",
            ["GiveAmmo"] = "Gives ammo.",
            ["GiveSupplyPoints"] = "Gives supply points.",
            ["GiveSupplyPointsUnrestricted"] = "Gives supply points past the limit.",
            ["GiveWeapon"] = "Gives a weapon by class name.",
            ["GiveItem"] = "Gives an item by class name.",
            ["RespawnMe"] = "Respawns you.",
            ["Revive"] = "Revives you.",
            ["Kill"] = "Kills you.",
            ["Slomo"] = "Game speed. 1 is normal, 0.5 is half speed.",
            ["FOV"] = "Sets the field of view.",
            ["ToggleDebugCamera"] = "Free camera. Run again to return.",
            ["ShowHUD"] = "Hides or shows the HUD.",
            ["JoinFaction"] = "Switches you to a team.",
            ["Say"] = "Sends a chat message.",
            ["Summon"] = "Spawns an actor by class name.",
            ["Teleport"] = "Teleports you to where you are looking.",
            ["MountPak"] = "Mounts a .pak file.",
            ["Pause"] = "Pauses the game.",
            ["PlayersOnly"] = "Freezes everything except players.",
            ["SpawnFireSupport"] = "Calls in fire support by class name.",
        };

        public static string NoteFor(string name) => name != null && Notes.TryGetValue(name, out var n) ? n : "";
    }
}

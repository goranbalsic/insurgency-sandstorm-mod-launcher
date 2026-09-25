using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SandstormModLauncher.Core;
using SandstormModLauncher.Models;

namespace SandstormModLauncher.Game
{
    /// <summary>Everything read from the game's own paks. Cached per game build.</summary>
    public sealed class OfficialData
    {
        public string CacheKey { get; set; }
        public int SchemaVersion { get; set; }
        public List<MutatorInfo> Mutators { get; set; } = new List<MutatorInfo>();
        public List<ScenarioInfo> Scenarios { get; set; } = new List<ScenarioInfo>();
        public Dictionary<string, string> MapThumbDay { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> MapThumbNight { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> GameModeAliases { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<string> DefaultConsoleKeys { get; set; } = new List<string>();
        public Dictionary<string, string> MutatorStrings { get; set; } = new Dictionary<string, string>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public static class GameCatalog
    {
        private const int Schema = 3;

        public static OfficialData Load(GameInstall install, string cacheDir, Action<string> progress)
        {
            if (!install.IsValid) return new OfficialData { Warnings = { "Insurgency: Sandstorm was not found." } };
            if (install.Demo) return Json.Deserialize<OfficialData>(File.ReadAllText(Path.Combine(GameInstall.DemoCacheDir, "official.json")));
            var paks = Directory.GetFiles(install.PaksDir, "*.pak").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            string key = string.Join("|", paks.Select(p => { var fi = new FileInfo(p); return fi.Name + ":" + fi.Length + ":" + fi.LastWriteTimeUtc.Ticks; }));
            string cacheFile = Path.Combine(cacheDir, "official.json");
            try
            {
                if (File.Exists(cacheFile))
                {
                    var cached = Json.Deserialize<OfficialData>(File.ReadAllText(cacheFile));
                    if (cached != null && cached.CacheKey == key && cached.SchemaVersion == Schema && cached.Scenarios.Count > 0
                        && cached.MapThumbDay.Values.All(File.Exists))
                        return cached;
                }
            }
            catch (Exception ex) { AppLog.Warn("Official cache unreadable: " + ex.Message); }

            progress?.Invoke("Reading the game's maps, scenarios and mutators...");
            var data = Scan(install, paks, cacheDir);
            data.CacheKey = key;
            data.SchemaVersion = Schema;
            try
            {
                Directory.CreateDirectory(cacheDir);
                File.WriteAllText(cacheFile, Json.Serialize(data, false));
            }
            catch (Exception ex) { AppLog.Warn("Could not save official cache: " + ex.Message); }
            return data;
        }

        private static OfficialData Scan(GameInstall install, List<string> pakPaths, string cacheDir)
        {
            var data = new OfficialData();
            var opened = new List<PakFile>();
            try
            {
                foreach (var p in pakPaths)
                {
                    try { opened.Add(PakFile.Open(p)); }
                    catch (Exception ex) { AppLog.Warn("Skipping pak " + Path.GetFileName(p) + ": " + ex.Message); }
                }
                byte[] Read(string path)
                {
                    foreach (var pk in opened)
                        if (pk.TryRead(path, out var d)) return d;
                    return null;
                }
                UPackage ReadPackage(string objectPath)
                {
                    string pkgPath = objectPath.Split('.')[0];
                    if (!pkgPath.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)) return null;
                    string rel = "Insurgency/Content/" + pkgPath.Substring(6);
                    var ua = Read(rel + ".uasset");
                    if (ua == null) return null;
                    return new UPackage(ua, Read(rel + ".uexp"));
                }

                // String tables used for display names.
                var tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var st in new[] { "/Game/UI/Strings/ST_Mutators.ST_Mutators", "/Game/UI/Strings/ST_Scenarios.ST_Scenarios", "/Game/UI/Strings/ST_Gamemodes.ST_Gamemodes" })
                {
                    try
                    {
                        var pkg = ReadPackage(st);
                        if (pkg != null) tables[st.Split('.')[0]] = pkg.ReadStringTable();
                    }
                    catch (Exception ex) { AppLog.Warn("String table " + st + ": " + ex.Message); }
                }
                string Lookup(string table, string key)
                {
                    string t = table.Split('.')[0];
                    return tables.TryGetValue(t, out var d) && d.TryGetValue(key, out var v) ? v : null;
                }
                if (tables.TryGetValue("/Game/UI/Strings/ST_Mutators", out var mutStrings)) data.MutatorStrings = mutStrings;

                // Asset registry: mutators and scenarios.
                var ar = Read("Insurgency/AssetRegistry.bin");
                if (ar == null) { data.Warnings.Add("The game's asset registry could not be read."); return data; }
                var assets = AssetRegistry.Parse(ar);
                AppLog.Info("Game asset registry: " + assets.Count + " assets");

                foreach (var a in assets)
                {
                    if (a.AssetClass != "Blueprint" || !string.Equals(a.Tag("PrimaryAssetType"), "Mutator", StringComparison.OrdinalIgnoreCase)) continue;
                    if ((ParseInt(a.Tag("ClassFlags")) & 1) != 0) continue;
                    string id = a.Tag("PrimaryAssetName") ?? a.AssetName;
                    string rawTitle = a.Tag("DisplayName") ?? "";
                    string display = UnrealText.Resolve(rawTitle, Lookup);
                    if (string.IsNullOrWhiteSpace(display)) display = UnrealText.Humanize(id);
                    string desc = null;
                    var m = Regex.Match(rawTitle, "\"([A-Za-z0-9_]+)Title\"");
                    if (m.Success && mutStrings != null)
                    {
                        foreach (var k in new[] { m.Groups[1].Value + "Description", id + "Description" })
                            if (mutStrings.TryGetValue(k, out var d)) { desc = d; break; }
                    }
                    if (desc == null && mutStrings != null && mutStrings.TryGetValue(id + "Description", out var d2)) desc = d2;
                    data.Mutators.Add(new MutatorInfo
                    {
                        Id = id, DisplayName = display, Description = desc ?? "", Author = "New World Interactive",
                        Source = ContentSource.Official, ModName = "Official", AssetPath = a.ObjectPath,
                        ConfigSection = "[" + a.PackageName + "." + a.AssetName + "_C]"
                    });
                }

                foreach (var a in assets)
                {
                    if (a.AssetClass != "ScenarioMultiplayer") continue;
                    var s = ScenarioFromAsset(a, ContentSource.Official, null, 0, Lookup);
                    if (s != null) data.Scenarios.Add(s);
                }
                data.Mutators = data.Mutators.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

                // Config: game mode aliases, console keys, map thumbnails.
                string engineIni = TextOf(Read("Insurgency/Config/DefaultEngine.ini"));
                foreach (Match m in Regex.Matches(engineIni, "GameModeClassAliases=\\(Name=\"([^\"]+)\",GameMode=\"([^\"]+)\"\\)"))
                    data.GameModeAliases[m.Groups[1].Value] = m.Groups[2].Value;

                string inputIni = TextOf(Read("Insurgency/Config/DefaultInput.ini"));
                data.DefaultConsoleKeys = UeIni.ReadArray(inputIni, "/Script/Engine.InputSettings", "ConsoleKeys", new List<string> { "Tilde" })
                                               .Where(k => !k.Equals("None", StringComparison.OrdinalIgnoreCase)).ToList();
                if (data.DefaultConsoleKeys.Count == 0) data.DefaultConsoleKeys.Add("Tilde");

                string gameIni = TextOf(Read("Insurgency/Config/DefaultGame.ini"));
                var thumbLine = Regex.Match(gameIni, @"^MapThumbnails=(.*)$", RegexOptions.Multiline);
                string thumbDir = Path.Combine(cacheDir, "thumbs");
                Directory.CreateDirectory(thumbDir);
                if (thumbLine.Success)
                {
                    foreach (Match m in Regex.Matches(thumbLine.Groups[1].Value,
                        "\\(\"(\\w+)\",\\s*\\(DefaultTexture=([^,\\)]+)(?:,LightingScenarioTextures=\\(\\(\"Night\",\\s*([^\\)]+)\\)\\))?"))
                    {
                        string mapKey = m.Groups[1].Value;
                        string day = SaveThumb(ReadPackage(m.Groups[2].Value.Trim()), Path.Combine(thumbDir, mapKey + "_day.png"));
                        if (day != null) data.MapThumbDay[mapKey] = day;
                        if (m.Groups[3].Success)
                        {
                            string night = SaveThumb(ReadPackage(m.Groups[3].Value.Trim()), Path.Combine(thumbDir, mapKey + "_night.png"));
                            if (night != null) data.MapThumbNight[mapKey] = night;
                        }
                    }
                }
                AppLog.Info($"Official data: {data.Mutators.Count} mutators, {data.Scenarios.Count} scenarios, {data.MapThumbDay.Count} thumbnails");
            }
            catch (Exception ex)
            {
                AppLog.Error("Scanning game paks failed", ex);
                data.Warnings.Add("Reading the game's files failed: " + ex.Message);
            }
            finally
            {
                foreach (var pk in opened) pk.Dispose();
            }
            return data;
        }

        public static ScenarioInfo ScenarioFromAsset(AssetData a, ContentSource source, string modName, long modId, Func<string, string, string> lookup)
        {
            string id = a.Tag("PrimaryAssetName") ?? a.AssetName;
            string level = a.Tag("Level") ?? "";
            if (string.IsNullOrEmpty(level) || level == "None") return null;
            if (a.ObjectPath.IndexOf("/Development/", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            int dot = level.LastIndexOf('.');
            string levelPkg = dot > 0 ? level.Substring(0, dot) : level;
            string levelName = levelPkg.Substring(levelPkg.LastIndexOf('/') + 1);
            string modeClass = a.Tag("GameMode") ?? "";
            var mm = Regex.Match(modeClass, "[\\./]([A-Za-z0-9_]+)'?$");
            string cls = mm.Success ? mm.Groups[1].Value : modeClass;
            var mp = Regex.Match(modeClass, "'\"?([^'\"]+)\"?'");
            string modePath = mp.Success ? mp.Groups[1].Value : modeClass;
            string side = UnrealText.Resolve(a.Tag("ScenarioName") ?? "", lookup).Trim();
            string category = a.ObjectPath.IndexOf("/Coop/", StringComparison.OrdinalIgnoreCase) >= 0 ? "Co-op"
                : a.ObjectPath.IndexOf("/Versus/", StringComparison.OrdinalIgnoreCase) >= 0 ? "Versus"
                : a.ObjectPath.IndexOf("/Training/", StringComparison.OrdinalIgnoreCase) >= 0 ? "Training"
                : IsCoopClass(cls) ? "Co-op" : "Versus";
            var parts = id.Split('_');
            string location = parts.Length > 1 ? parts[1] : levelName;
            return new ScenarioInfo
            {
                Id = id, Level = levelPkg, MapKey = a.Tag("Map") is string mk && mk.Length > 0 ? mk : levelName,
                GameModeClass = cls, GameModePath = modePath, GameModeName = ModeName(cls), Side = side, Category = category,
                IsCoop = category == "Co-op", Source = source, ModName = modName, ModId = modId, Location = location
            };
        }

        public static bool IsCoopClass(string cls)
        {
            string c = (cls ?? "").ToLowerInvariant();
            return c.Contains("checkpoint") || c.Contains("outpost") || c.Contains("survival") || c.Contains("coop");
        }

        public static string ModeName(string cls)
        {
            switch (cls)
            {
                case "INSCheckpointGameMode": return "Checkpoint";
                case "INSCheckpointHardcoreGameMode": return "Hardcore Checkpoint";
                case "INSOutpostGameMode": return "Outpost";
                case "INSSurvivalGameMode": return "Survival";
                case "INSPushGameMode": return "Push";
                case "INSFirefightGameMode": return "Firefight";
                case "INSFrontlineGameMode": return "Frontline";
                case "DominationGameMode": return "Domination";
                case "INSAmbushGameMode": return "Ambush";
                case "INSDefuseGameMode": return "Defusal";
                case "INSTeamDeathmatchGameMode": return "Team Deathmatch";
                case "INSFreeForAllMode": return "Free For All";
                case "INSRangeMode": return "Firing Range";
            }
            string s = cls ?? "";
            if (s.StartsWith("BP_")) s = s.Substring(3);
            if (s.EndsWith("_C")) s = s.Substring(0, s.Length - 2);
            if (s.StartsWith("INS")) s = s.Substring(3);
            s = s.Replace("GameMode", " ").Replace("Gamemode", " ");
            if (s.EndsWith("Mode")) s = s.Substring(0, s.Length - 4);
            return UnrealText.Humanize(s.Replace("_", " ").Trim()).Replace("  ", " ");
        }

        private static string SaveThumb(UPackage pkg, string file)
        {
            try
            {
                if (pkg == null) return null;
                var tex = pkg.ReadInlineTexture();
                if (tex == null) return null;
                var pixels = Dxt.Decode(tex.Value.Format, tex.Value.Width, tex.Value.Height, tex.Value.Pixels);
                if (pixels == null) return null;
                var bmp = BitmapSource.Create(tex.Value.Width, tex.Value.Height, 96, 96, PixelFormats.Bgra32, null, pixels, tex.Value.Width * 4);
                bmp.Freeze();
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using (var fs = File.Create(file)) enc.Save(fs);
                return file;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Thumbnail " + Path.GetFileName(file) + ": " + ex.Message);
                return null;
            }
        }

        private static string TextOf(byte[] b)
        {
            if (b == null) return "";
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
            return Encoding.UTF8.GetString(b);
        }

        private static long ParseInt(string s) => long.TryParse(s, out var v) ? v : 0;

        /// <summary>Groups scenarios into maps and picks friendly map names ("Buhriz" plays as "Tideway").</summary>
        public static List<MapInfo> BuildMaps(IEnumerable<ScenarioInfo> scenarios, OfficialData official)
        {
            var maps = new Dictionary<string, MapInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in scenarios)
            {
                string key = s.Source == ContentSource.Official ? s.MapKey : s.Level;
                if (!maps.TryGetValue(key, out var m))
                {
                    m = new MapInfo
                    {
                        Key = key, Level = s.Level, LevelName = s.Level.Substring(s.Level.LastIndexOf('/') + 1),
                        Source = s.Source, ModName = s.ModName
                    };
                    maps[key] = m;
                }
                m.Scenarios.Add(s);
            }
            foreach (var m in maps.Values)
            {
                var loc = m.Scenarios.Where(x => x.Category != "Training").GroupBy(x => x.Location, StringComparer.OrdinalIgnoreCase)
                           .OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? m.LevelName;
                m.DisplayName = UnrealText.Humanize(loc);
                if (official != null)
                {
                    string day = official.MapThumbDay.FirstOrDefault(kv => kv.Key.Equals(m.Key, StringComparison.OrdinalIgnoreCase) || kv.Key.Equals(m.LevelName, StringComparison.OrdinalIgnoreCase)).Value;
                    string night = official.MapThumbNight.FirstOrDefault(kv => kv.Key.Equals(m.Key, StringComparison.OrdinalIgnoreCase) || kv.Key.Equals(m.LevelName, StringComparison.OrdinalIgnoreCase)).Value;
                    m.ThumbDay = day;
                    m.ThumbNight = night ?? day;
                }
                m.Scenarios = m.Scenarios
                    .OrderBy(x => x.Category == "Co-op" ? 0 : x.Category == "Versus" ? 1 : 2)
                    .ThenBy(x => ModeOrder(x.GameModeName)).ThenBy(x => x.Side).ToList();
            }
            return maps.Values.OrderBy(m => m.Source).ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static int ModeOrder(string mode)
        {
            string[] order = { "Checkpoint", "Hardcore Checkpoint", "Outpost", "Survival", "Push", "Frontline", "Firefight", "Domination", "Skirmish", "Ambush", "Defusal", "Team Deathmatch", "Free For All" };
            int i = Array.IndexOf(order, mode);
            return i < 0 ? 99 : i;
        }
    }
}

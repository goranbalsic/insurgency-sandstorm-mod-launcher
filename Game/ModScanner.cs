using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SandstormModLauncher.Core;
using SandstormModLauncher.Models;

namespace SandstormModLauncher.Game
{
    /// <summary>What one pak file contributes (cached by file size + timestamp).</summary>
    public sealed class PakScan
    {
        public string Key { get; set; }
        public List<string> Roots { get; set; } = new List<string>();
        public string InGameName { get; set; }
        public string WebsiteUrl { get; set; }
        public bool RequiredByClients { get; set; }
        public List<MutatorInfo> Mutators { get; set; } = new List<MutatorInfo>();
        public List<ScenarioInfo> Scenarios { get; set; } = new List<ScenarioInfo>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public sealed class ModScanCache
    {
        public int Schema { get; set; }
        public Dictionary<string, PakScan> Paks { get; set; } = new Dictionary<string, PakScan>(StringComparer.OrdinalIgnoreCase);
    }

    public static class ModScanner
    {
        private const int Schema = 4;

        public static List<ModInfo> Scan(GameInstall install, IEnumerable<string> extraFolders, string cacheDir, Action<string> progress)
        {
            var mods = new List<ModInfo>();
            string cacheFile = Path.Combine(cacheDir, "mods.json");
            var cache = new ModScanCache { Schema = Schema };
            try
            {
                if (File.Exists(cacheFile))
                {
                    var c = Json.Deserialize<ModScanCache>(File.ReadAllText(cacheFile));
                    if (c != null && c.Schema == Schema) cache = c;
                }
            }
            catch { }
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. mod.io (current SDK layout)
            string root = GameInstall.ModioRoot;
            var known = new Dictionary<string, ModInfo>(StringComparer.OrdinalIgnoreCase);
            string state = Path.Combine(root, "metadata", "state.json");
            if (File.Exists(state))
            {
                try
                {
                    var json = Json.Parse(ReadShared(state));
                    foreach (var m in json.Get("Mods").Arr())
                    {
                        var mod = FromModio(m);
                        if (mod.Folder == null) continue;
                        known[Path.GetFullPath(mod.Folder).TrimEnd('\\')] = mod;
                    }
                }
                catch (Exception ex) { AppLog.Warn("mod.io state.json unreadable: " + ex.Message); }
            }
            var folders = new List<string>();
            string modsDir = Path.Combine(root, "mods");
            if (Directory.Exists(modsDir)) folders.AddRange(Directory.GetDirectories(modsDir));
            foreach (var k in known.Keys) if (!folders.Any(f => Same(f, k))) folders.Add(k);

            // 2. legacy layout and user folders
            if (install.LegacyModioDir != null && Directory.Exists(install.LegacyModioDir))
                folders.AddRange(PakFolders(install.LegacyModioDir));
            foreach (var extra in extraFolders ?? Enumerable.Empty<string>())
                if (!string.IsNullOrWhiteSpace(extra) && Directory.Exists(extra)) folders.AddRange(PakFolders(extra));

            int n = 0;
            folders = folders.Select(f => { try { return Path.GetFullPath(f).TrimEnd('\\'); } catch { return f; } }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var folder in folders)
            {
                n++;
                string full = Path.GetFullPath(folder).TrimEnd('\\');
                var paks = Directory.Exists(full) ? Directory.GetFiles(full, "*.pak", SearchOption.TopDirectoryOnly) : new string[0];
                known.TryGetValue(full, out var mod);
                if (mod == null)
                {
                    if (paks.Length == 0) continue;
                    mod = new ModInfo { Folder = full, Name = Path.GetFileName(full), State = "Local folder" };
                    if (long.TryParse(Path.GetFileName(full), out long id)) { mod.Id = id; mod.State = "Not registered by the game yet"; }
                    else ReadLegacyJson(full, mod);
                }
                mod.LogoFile = LocalLogo(root, mod.Id, full);
                progress?.Invoke($"Scanning mods ({n}/{folders.Count}): {mod.Name}");
                foreach (var pak in paks)
                {
                    mod.Paks.Add(Path.GetFileName(pak));
                    var fi = new FileInfo(pak);
                    string key = pak + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
                    if (!cache.Paks.TryGetValue(pak, out var scan) || scan.Key != key)
                    {
                        scan = ScanPak(pak, mod);
                        scan.Key = key;
                        cache.Paks[pak] = scan;
                    }
                    used.Add(pak);
                    Merge(mod, scan);
                }
                if (mod.SizeOnDisk == 0) mod.SizeOnDisk = paks.Sum(p => new FileInfo(p).Length);
                if (string.IsNullOrEmpty(mod.Name) || mod.Name == Path.GetFileName(full)) mod.Name = mod.InGameName ?? mod.Name;
                Tidy(mod.Mutators);
                foreach (var mu in mod.Mutators) { mu.ModName = mod.Name; mu.ModId = mod.Id; }
                foreach (var sc in mod.Scenarios) { sc.ModName = mod.Name; sc.ModId = mod.Id; }
                if (paks.Length == 0)
                    mod.Warnings.Add(Directory.Exists(full)
                        ? "No .pak files in its folder yet. The game may still be downloading it."
                        : "Its files are not on this PC. The game downloads them again the next time it starts, as long as you are still subscribed.");
                mods.Add(mod);
            }

            foreach (var stale in cache.Paks.Keys.Where(k => !used.Contains(k)).ToList()) cache.Paks.Remove(stale);
            try { Directory.CreateDirectory(cacheDir); File.WriteAllText(cacheFile, Json.Serialize(cache, false)); } catch { }
            return mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool Same(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        private static IEnumerable<string> PakFolders(string root)
        {
            var list = new List<string>();
            try
            {
                if (Directory.GetFiles(root, "*.pak").Length > 0) list.Add(root);
                foreach (var d in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
                    if (Directory.GetFiles(d, "*.pak").Length > 0) list.Add(d);
            }
            catch { }
            return list;
        }

        /// <summary>A logo the game already cached on disk (or an image inside the mod folder). Never downloads anything.</summary>
        private static string LocalLogo(string modioRoot, long id, string folder)
        {
            var dirs = new List<string>();
            if (id > 0) dirs.Add(Path.Combine(modioRoot, "cache", "mods", id.ToString(), "logos"));
            dirs.Add(folder);
            foreach (var dir in dirs)
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    var files = Directory.GetFiles(dir)
                        .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => Path.GetFileName(f).IndexOf("Thumb320", StringComparison.OrdinalIgnoreCase) >= 0)
                        .ThenByDescending(f => Path.GetFileName(f).IndexOf("logo", StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToList();
                    if (files.Count > 0) return files[0];
                }
                catch { }
            }
            return null;
        }

        private static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs)) return r.ReadToEnd();
        }

        private static ModInfo FromModio(object m)
        {
            var p = m.Get("Profile");
            var mod = new ModInfo
            {
                Id = m.Get("ID").Long(),
                Folder = m.Get("PathOnDisk").Str(),
                SizeOnDisk = m.Get("SizeOnDisk").Long(),
                State = StateName(m.Get("State").Long(-1)),
                Name = p.Get("name").Str(),
                Summary = p.Get("summary").Str(),
                Description = p.Get("description_plaintext").Str(),
                Author = p.Path("submitted_by", "username").Str(),
                Version = p.Path("modfile", "version").Str(),
            };
            long updated = p.Get("date_updated").Long();
            if (updated > 0) mod.Updated = DateTimeOffset.FromUnixTimeSeconds(updated).LocalDateTime;
            foreach (var t in p.Get("tags").Arr()) { string tn = t.Get("name").Str(); if (!string.IsNullOrEmpty(tn)) mod.Tags.Add(tn); }
            return mod;
        }

        private static string StateName(long s)
        {
            switch (s) // mod.io SDK ModState
            {
                case 0: return "Install pending";
                case 1: return "Installed";
                case 2: return "Update pending";
                case 3: return "Downloading";
                case 4: return "Extracting";
                case 5: return "Uninstall pending";
                default: return s < 0 ? "" : "State " + s;
            }
        }

        private static void ReadLegacyJson(string folder, ModInfo mod)
        {
            try
            {
                foreach (var f in Directory.GetFiles(folder, "*.json"))
                {
                    var j = Json.Parse(File.ReadAllText(f));
                    if (j.Get("name") == null) continue;
                    mod.Name = j.Get("name").Str();
                    mod.Id = j.Get("id").Long();
                    mod.Summary = j.Get("summary").Str();
                    mod.Author = j.Path("submitted_by", "username").Str() ?? j.Path("submittedBy", "username").Str();
                    foreach (var t in j.Get("tags").Arr()) mod.Tags.Add(t.Get("name").Str());
                    mod.State = "Legacy mod folder";
                    return;
                }
            }
            catch { }
        }

        /// <summary>Cleans up author/description quirks and makes duplicate display names distinct.</summary>
        private static void Tidy(List<MutatorInfo> list)
        {
            foreach (var m in list)
            {
                // Some mods type the description into the Author field.
                if (string.IsNullOrWhiteSpace(m.Description) && m.Author != null && (m.Author.Length > 24 || m.Author.IndexOfAny(new[] { '%', '.', '+' }) >= 0))
                {
                    m.Description = m.Author;
                    m.Author = null;
                }
            }
            foreach (var group in list.GroupBy(m => m.DisplayName ?? "", StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                foreach (var m in group)
                {
                    string fromId = UnrealText.Humanize(m.Id);
                    if (!fromId.Equals(m.DisplayName, StringComparison.OrdinalIgnoreCase)) m.DisplayName = fromId;
                }
        }

        private static void Merge(ModInfo mod, PakScan scan)
        {
            if (mod.PackageRoot == null && scan.Roots.Count > 0) mod.PackageRoot = scan.Roots[0];
            mod.InGameName = mod.InGameName ?? scan.InGameName;
            mod.WebsiteUrl = mod.WebsiteUrl ?? scan.WebsiteUrl;
            mod.RequiredByClients |= scan.RequiredByClients;
            foreach (var m in scan.Mutators)
                if (!mod.Mutators.Any(x => x.Id.Equals(m.Id, StringComparison.OrdinalIgnoreCase))) mod.Mutators.Add(m);
            foreach (var s in scan.Scenarios)
                if (!mod.Scenarios.Any(x => x.Id.Equals(s.Id, StringComparison.OrdinalIgnoreCase))) mod.Scenarios.Add(s);
            foreach (var w in scan.Warnings) if (!mod.Warnings.Contains(w)) mod.Warnings.Add(w);
        }

        public static PakScan ScanPak(string path, ModInfo mod)
        {
            var scan = new PakScan();
            PakFile pak = null;
            try
            {
                pak = PakFile.Open(path);
                if (pak.IndexEncrypted) { scan.Warnings.Add(Path.GetFileName(path) + " has an encrypted index."); return scan; }

                // Map package roots (/ISMCm/) to content folders inside the pak.
                var contentPrefix = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in pak.Entries.Keys)
                {
                    int ci = e.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
                    if (ci <= 0) continue;
                    string before = e.Substring(0, ci);
                    string rootName = before.Substring(before.LastIndexOf('/') + 1);
                    if (!contentPrefix.ContainsKey(rootName)) contentPrefix[rootName] = e.Substring(0, ci + 9);
                }
                UPackage ReadPkg(string packageName)
                {
                    if (string.IsNullOrEmpty(packageName) || packageName[0] != '/') return null;
                    int slash = packageName.IndexOf('/', 1);
                    if (slash < 0) return null;
                    string r = packageName.Substring(1, slash - 1), rest = packageName.Substring(slash + 1);
                    if (!contentPrefix.TryGetValue(r, out var prefix)) return null;
                    if (!pak.TryRead(prefix + rest + ".uasset", out var ua)) return null;
                    pak.TryRead(prefix + rest + ".uexp", out var ue);
                    try { return new UPackage(ua, ue); } catch { return null; }
                }

                var registries = pak.Entries.Keys.Where(k => k.EndsWith("AssetRegistry.bin", StringComparison.OrdinalIgnoreCase)).ToList();
                var assets = new List<AssetData>();
                foreach (var ar in registries)
                {
                    if (!pak.TryRead(ar, out var bytes)) { scan.Warnings.Add("Asset registry in " + Path.GetFileName(path) + " uses unsupported compression."); continue; }
                    try { assets.AddRange(AssetRegistry.Parse(bytes)); }
                    catch (Exception ex) { scan.Warnings.Add("Asset registry could not be read: " + ex.Message); }
                }

                // ModData: registered primary asset folders.
                var mutatorDirs = new List<string>();
                var scenarioDirs = new List<string>();
                foreach (var md in assets.Where(a => a.AssetClass == "ModData"))
                {
                    string r = RootOf(md.PackageName);
                    if (r != null && !scan.Roots.Contains(r)) scan.Roots.Add(r);
                    var pkg = ReadPkg(md.PackageName);
                    var main = pkg?.MainExport();
                    if (main == null) continue;
                    var props = pkg.ReadProperties(main);
                    if (props.TryGetValue("InGameName", out var ign) && ign is string s1 && s1.Length > 0) scan.InGameName = UnrealText.Resolve(s1);
                    if (props.TryGetValue("WebsiteUrl", out var web) && web is string s2) scan.WebsiteUrl = s2;
                    if (props.TryGetValue("bRequiredByClients", out var req) && req is bool b) scan.RequiredByClients = b;
                    if (props.TryGetValue("PrimaryAssetDirectories", out var dirs) && dirs is List<object> list)
                        foreach (var item in list.OfType<Dictionary<string, object>>())
                        {
                            string type = item.TryGetValue("AssetType", out var t) ? t?.ToString() ?? "" : "";
                            string dir = item.TryGetValue("Directory", out var d) ? DirString(d) : "";
                            if (dir.Length == 0) continue;
                            string full = "/" + r + "/" + dir.Trim('/') + "/";
                            if (type.EndsWith("Mutator", StringComparison.OrdinalIgnoreCase)) mutatorDirs.Add(full);
                            else if (type.IndexOf("Scenario", StringComparison.OrdinalIgnoreCase) >= 0) scenarioDirs.Add(full);
                        }
                }
                // Mutators
                var mutatorAssets = assets.Where(a => a.AssetClass == "Blueprint" &&
                    (string.Equals(a.Tag("PrimaryAssetType"), "Mutator", StringComparison.OrdinalIgnoreCase) ||
                     (a.Tag("NativeParentClass") ?? "").EndsWith(".Mutator'", StringComparison.OrdinalIgnoreCase))).ToList();
                var parents = new HashSet<string>(mutatorAssets.Select(a => a.Tag("ParentClass") ?? ""), StringComparer.OrdinalIgnoreCase);
                foreach (var a in mutatorAssets)
                {
                    if ((ParseInt(a.Tag("ClassFlags")) & 1) != 0) continue;
                    string id = a.Tag("PrimaryAssetName") ?? a.AssetName;
                    string generated = a.PackageName + "." + a.AssetName + "_C";
                    bool registered = mutatorDirs.Count == 0 || mutatorDirs.Any(d => (a.PackagePath + "/").StartsWith(d, StringComparison.OrdinalIgnoreCase));
                    var mi = new MutatorInfo
                    {
                        Id = id,
                        DisplayName = UnrealText.Resolve(a.Tag("DisplayName") ?? ""),
                        Source = ContentSource.Mod,
                        AssetPath = a.ObjectPath,
                        ConfigSection = "[" + generated + "]",
                        IsBaseClass = parents.Any(p => p.IndexOf(generated, StringComparison.OrdinalIgnoreCase) >= 0)
                                      && (id.IndexOf("base", StringComparison.OrdinalIgnoreCase) >= 0 || string.IsNullOrWhiteSpace(a.Tag("DisplayName"))),
                        Registered = registered,
                    };
                    var pkg = ReadPkg(a.PackageName);
                    if (pkg != null)
                    {
                        var cdo = pkg.FindExport(e => e.Name.Equals("Default__" + a.AssetName + "_C", StringComparison.OrdinalIgnoreCase));
                        if (cdo != null)
                        {
                            var props = pkg.ReadProperties(cdo);
                            if (props.TryGetValue("Description", out var d) && d is string ds) mi.Description = UnrealText.Resolve(ds);
                            if (props.TryGetValue("Author", out var au) && au is string aus) mi.Author = aus;
                            if (string.IsNullOrWhiteSpace(mi.DisplayName) && props.TryGetValue("DisplayName", out var dn) && dn is string dns) mi.DisplayName = UnrealText.Resolve(dns);
                        }
                    }
                    if (string.IsNullOrWhiteSpace(mi.DisplayName)) mi.DisplayName = UnrealText.Humanize(id);
                    if (!registered) mi.Warning = "Not in a folder the mod registers for mutators, so the game cannot load it by name.";
                    scan.Mutators.Add(mi);
                }

                // Scenarios / maps
                foreach (var a in assets.Where(x => x.AssetClass == "ScenarioMultiplayer"))
                {
                    if (scenarioDirs.Count > 0 && !scenarioDirs.Any(d => (a.PackagePath + "/").StartsWith(d, StringComparison.OrdinalIgnoreCase))) continue;
                    var s = GameCatalog.ScenarioFromAsset(a, ContentSource.Mod, mod?.Name, mod?.Id ?? 0, null);
                    if (s != null && !s.Level.StartsWith("/Game/Maps/Utility", StringComparison.OrdinalIgnoreCase)) scan.Scenarios.Add(s);
                }

                // No readable registry: fall back to file names in Mutators folders.
                if (assets.Count == 0)
                {
                    foreach (var e in pak.Entries.Keys.Where(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && k.IndexOf("/Mutators/", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        string name = Path.GetFileNameWithoutExtension(e);
                        if (scan.Mutators.Any(m => m.Id.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                        scan.Mutators.Add(new MutatorInfo
                        {
                            Id = name, DisplayName = UnrealText.Humanize(name), Source = ContentSource.Mod,
                            Warning = "Detected from the file name only; check the mod page for the exact mutator name."
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                scan.Warnings.Add(Path.GetFileName(path) + ": " + ex.Message);
                AppLog.Warn("Mod pak " + path + ": " + ex.Message);
            }
            finally { pak?.Dispose(); }
            return scan;
        }

        private static string DirString(object d)
        {
            if (d is string s) return s;
            if (d is Dictionary<string, object> dict && dict.TryGetValue("Path", out var p)) return p?.ToString() ?? "";
            return d?.ToString() ?? "";
        }

        private static string RootOf(string packageName)
        {
            if (string.IsNullOrEmpty(packageName) || packageName[0] != '/') return null;
            int slash = packageName.IndexOf('/', 1);
            return slash > 0 ? packageName.Substring(1, slash - 1) : null;
        }

        private static long ParseInt(string s) => long.TryParse(s, out var v) ? v : 0;
    }
}

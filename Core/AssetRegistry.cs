using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SandstormModLauncher.Core
{
    public sealed class AssetData
    {
        public string ObjectPath, PackagePath, AssetClass, PackageName, AssetName;
        public Dictionary<string, string> Tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Tag(string key) => Tags.TryGetValue(key, out var v) ? v : null;
    }

    /// <summary>
    /// Reads cooked AssetRegistry.bin files: the UE 4.26/4.27 "fixed tag" format (version 8)
    /// and the older name-table format (versions 1-7) used by mods cooked with older kits.
    /// </summary>
    public static class AssetRegistry
    {
        public static List<AssetData> Parse(byte[] data)
        {
            var r = new BinReader(data);
            r.Skip(16); // FAssetRegistryVersion guid
            int version = r.I32();
            if (version >= 8) return ParseFixedTags(r);
            return ParseLegacy(r, version);
        }

        // ----------------------------------------------------------------- legacy (<= 7)

        private static List<AssetData> ParseLegacy(BinReader r, int version)
        {
            long nameTableOffset = r.I64();
            int assetsStart = r.Pos;
            var names = ReadNameTable(r.Data, (int)nameTableOffset, true) ?? ReadNameTable(r.Data, (int)nameTableOffset, false)
                        ?? throw new InvalidDataException("Unreadable asset registry name table");
            r.Pos = assetsStart;

            string Name()
            {
                int idx = r.I32(); int num = r.I32();
                if (idx < 0 || idx >= names.Count) return "";
                return num == 0 ? names[idx] : names[idx] + "_" + (num - 1);
            }

            int count = r.I32();
            var list = new List<AssetData>(Math.Max(0, count));
            for (int i = 0; i < count; i++)
            {
                var a = new AssetData { ObjectPath = Name(), PackagePath = Name(), AssetClass = Name(), PackageName = Name(), AssetName = Name() };
                int tags = r.I32();
                for (int t = 0; t < tags; t++) { string k = Name(); a.Tags[k] = r.FString(); }
                int chunks = r.I32(); r.Skip(4 * chunks);
                r.U32(); // package flags
                list.Add(a);
            }
            return list;
        }

        private static List<string> ReadNameTable(byte[] data, int offset, bool withHashes)
        {
            try
            {
                var r = new BinReader(data, offset);
                int n = r.I32();
                if (n < 0 || n > 10_000_000) return null;
                var names = new List<string>(n);
                for (int i = 0; i < n; i++)
                {
                    names.Add(r.FString());
                    if (withHashes) r.Skip(4);
                }
                return r.Pos == data.Length ? names : (withHashes ? null : names);
            }
            catch { return null; }
        }

        // ------------------------------------------------------------- fixed tags (>= 8)

        private static List<AssetData> ParseFixedTags(BinReader r)
        {
            // Name batch
            uint nameCount = r.U32();
            var names = new List<string>((int)Math.Min(nameCount, 1_000_000));
            if (nameCount > 0)
            {
                uint stringBytes = r.U32();
                r.U64(); // hash version
                r.Skip((int)(8 * nameCount));
                int headersStart = r.Pos;
                int strPos = headersStart + (int)(2 * nameCount);
                for (int i = 0; i < nameCount; i++)
                {
                    byte h0 = r.Data[headersStart + 2 * i], h1 = r.Data[headersStart + 2 * i + 1];
                    bool wide = (h0 & 0x80) != 0;
                    int len = ((h0 & 0x7f) << 8) | h1;
                    if (wide)
                    {
                        names.Add(Encoding.Unicode.GetString(r.Data, strPos, len * 2));
                        strPos += len * 2;
                    }
                    else
                    {
                        names.Add(BinReader.Latin1.GetString(r.Data, strPos, len));
                        strPos += len;
                    }
                }
                r.Pos = headersStart + (int)(2 * nameCount) + (int)stringBytes;
            }

            string FName()
            {
                uint idx = r.U32();
                uint num = 0;
                if ((idx & 0x80000000u) != 0) { idx &= 0x7fffffff; num = r.U32(); }
                string s = idx < names.Count ? names[(int)idx] : "";
                return num == 0 ? s : s + "_" + (num - 1);
            }
            string EntryName()
            {
                uint idx = r.U32();
                return idx < names.Count ? names[(int)idx] : "";
            }

            uint magic = r.U32();
            if (magic != 0x12345679 && magic != 0x12345678) throw new InvalidDataException("Bad asset registry tag store");
            int nNumberlessNames = r.I32(), nNames = r.I32(), nNumberlessExport = r.I32(), nExport = r.I32(), nTexts = r.I32();
            int nAnsiOff = r.I32(), nWideOff = r.I32(), nAnsi = r.I32(), nWide = r.I32(), nNumberlessPairs = r.I32(), nPairs = r.I32();

            var texts = new List<string>();
            if (magic == 0x12345679)
            {
                r.U32(); // text data bytes
                for (int i = 0; i < nTexts; i++) texts.Add(r.FString());
            }
            var numberless = new string[nNumberlessNames];
            for (int i = 0; i < nNumberlessNames; i++) numberless[i] = EntryName();
            var fnames = new string[nNames];
            for (int i = 0; i < nNames; i++) fnames[i] = FName();
            var nexp = new string[nNumberlessExport];
            for (int i = 0; i < nNumberlessExport; i++) { string c = EntryName(), o = EntryName(), p = EntryName(); nexp[i] = c + "'" + p + "." + o + "'"; }
            var exp = new string[nExport];
            for (int i = 0; i < nExport; i++) { string c = FName(), o = FName(), p = FName(); exp[i] = c + "'" + p + "." + o + "'"; }
            if (magic == 0x12345678)
                for (int i = 0; i < nTexts; i++) texts.Add(r.FString());

            var ansiOff = new uint[nAnsiOff];
            for (int i = 0; i < nAnsiOff; i++) ansiOff[i] = r.U32();
            var wideOff = new uint[nWideOff];
            for (int i = 0; i < nWideOff; i++) wideOff[i] = r.U32();
            int ansiStart = r.Pos; r.Skip(nAnsi);
            int wideStart = r.Pos; r.Skip(nWide * 2);

            string Ansi(int i)
            {
                int a = ansiStart + (int)ansiOff[i], e = a;
                while (e < ansiStart + nAnsi && r.Data[e] != 0) e++;
                return BinReader.Latin1.GetString(r.Data, a, e - a);
            }
            string Wide(int i)
            {
                int a = wideStart + (int)wideOff[i] * 2, e = a;
                while (e + 1 < wideStart + nWide * 2 && (r.Data[e] != 0 || r.Data[e + 1] != 0)) e += 2;
                return Encoding.Unicode.GetString(r.Data, a, e - a);
            }
            string Value(uint v)
            {
                int type = (int)(v & 7), i = (int)(v >> 3);
                try
                {
                    switch (type)
                    {
                        case 0: return Ansi(i);
                        case 1: return Wide(i);
                        case 2: return numberless[i];
                        case 3: return fnames[i];
                        case 4: return nexp[i];
                        case 5: return exp[i];
                        case 6: return texts[i];
                    }
                }
                catch { }
                return "";
            }

            var numberlessPairs = new KeyValuePair<string, uint>[nNumberlessPairs];
            for (int i = 0; i < nNumberlessPairs; i++) { string k = EntryName(); numberlessPairs[i] = new KeyValuePair<string, uint>(k, r.U32()); }
            var pairs = new KeyValuePair<string, uint>[nPairs];
            for (int i = 0; i < nPairs; i++) { string k = FName(); pairs[i] = new KeyValuePair<string, uint>(k, r.U32()); }
            if (r.U32() != 0x87654321) throw new InvalidDataException("Bad asset registry tag store end");

            int count = r.I32();
            var list = new List<AssetData>(Math.Max(0, count));
            for (int i = 0; i < count; i++)
            {
                var a = new AssetData { ObjectPath = FName(), PackagePath = FName(), AssetClass = FName(), PackageName = FName(), AssetName = FName() };
                ulong handle = r.U64();
                bool numberlessKeys = (handle >> 63) != 0;
                int num = (int)((handle >> 32) & 0xffff);
                int begin = (int)(handle & 0xffffffff);
                var src = numberlessKeys ? numberlessPairs : pairs;
                for (int k = begin; k < begin + num && k < src.Length; k++) a.Tags[src[k].Key] = Value(src[k].Value);
                int bundles = r.I32();
                for (int b = 0; b < bundles; b++)
                {
                    FName();
                    int assets = r.I32();
                    for (int x = 0; x < assets; x++) { FName(); r.FString(); }
                }
                int chunks = r.I32(); r.Skip(4 * chunks);
                r.U32();
                list.Add(a);
            }
            return list;
        }
    }
}

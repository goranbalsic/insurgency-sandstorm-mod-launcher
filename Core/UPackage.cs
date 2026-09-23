using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SandstormModLauncher.Core
{
    public sealed class UExport
    {
        public int ClassIndex, SuperIndex, OuterIndex;
        public string Name;
        public long SerialSize, SerialOffset;
    }

    public sealed class UImport
    {
        public string ClassPackage, ClassName, ObjectName;
        public int OuterIndex;
    }

    /// <summary>
    /// Minimal cooked-package reader: summary, names, imports, exports and tagged properties.
    /// Enough to read ModData, mutator defaults, rulesets, string tables and textures.
    /// </summary>
    public sealed class UPackage
    {
        public readonly byte[] Data;
        public int FileVersion;
        public uint PackageFlags;
        public int TotalHeaderSize;
        public long BulkDataStartOffset;
        public readonly List<string> Names = new List<string>();
        public readonly List<UImport> Imports = new List<UImport>();
        public readonly List<UExport> Exports = new List<UExport>();

        public bool UnversionedProperties => (PackageFlags & 0x2000) != 0;

        public UPackage(byte[] uasset, byte[] uexp)
        {
            if (uexp != null && uexp.Length > 0)
            {
                Data = new byte[uasset.Length + uexp.Length];
                Buffer.BlockCopy(uasset, 0, Data, 0, uasset.Length);
                Buffer.BlockCopy(uexp, 0, Data, uasset.Length, uexp.Length);
            }
            else Data = uasset;
            ReadSummary();
        }

        private void ReadSummary()
        {
            var r = new BinReader(Data);
            if (r.U32() != 0x9E2A83C1) throw new InvalidDataException("Not an Unreal package");
            int legacy = r.I32();
            if (legacy != -4) r.I32();
            FileVersion = r.I32();
            if (FileVersion == 0) FileVersion = 522; // unversioned cook: engine 4.26/4.27
            if (legacy <= -8) r.I32();
            r.I32(); // licensee
            if (legacy <= -2)
            {
                int n = r.I32();
                for (int i = 0; i < n; i++)
                {
                    if (legacy == -2) { r.I32(); r.I32(); }
                    else if (legacy >= -5) { r.Skip(16); r.I32(); r.FString(); }
                    else { r.Skip(16); r.I32(); }
                }
            }
            TotalHeaderSize = r.I32();
            r.FString();
            PackageFlags = r.U32();
            int nameCount = r.I32(), nameOffset = r.I32();
            if (FileVersion >= 516 && (PackageFlags & 0x80000000) == 0) r.FString();
            if (FileVersion >= 459) { r.I32(); r.I32(); }
            int exportCount = r.I32(), exportOffset = r.I32();
            int importCount = r.I32(), importOffset = r.I32();
            r.I32();
            if (FileVersion >= 384) { r.I32(); r.I32(); }
            if (FileVersion >= 510) r.I32();
            r.I32();
            r.Skip(16);
            if ((PackageFlags & 0x80000000) == 0 && FileVersion >= 518) { r.Skip(16); if (FileVersion < 520) r.Skip(16); }
            int gens = r.I32(); r.Skip(gens * 8);
            if (FileVersion >= 336) { r.U16(); r.U16(); r.U16(); r.U32(); r.FString(); } else r.I32();
            if (FileVersion >= 444) { r.U16(); r.U16(); r.U16(); r.U32(); r.FString(); }
            r.U32();
            int chunks = r.I32(); r.Skip(chunks * 16);
            r.U32();
            int extra = r.I32(); for (int i = 0; i < extra; i++) r.FString();
            if (legacy > -7) r.I32();
            r.I32();
            BulkDataStartOffset = r.I64();

            r.Pos = nameOffset;
            for (int i = 0; i < nameCount; i++) { Names.Add(r.FString()); if (FileVersion >= 504) r.Skip(4); }

            r.Pos = importOffset;
            for (int i = 0; i < importCount; i++)
                Imports.Add(new UImport { ClassPackage = FName(r), ClassName = FName(r), OuterIndex = r.I32(), ObjectName = FName(r) });

            r.Pos = exportOffset;
            for (int i = 0; i < exportCount; i++)
            {
                var e = new UExport { ClassIndex = r.I32(), SuperIndex = r.I32() };
                if (FileVersion >= 508) r.I32();
                e.OuterIndex = r.I32();
                e.Name = FName(r);
                r.U32();
                if (FileVersion >= 511) { e.SerialSize = r.I64(); e.SerialOffset = r.I64(); }
                else { e.SerialSize = r.I32(); e.SerialOffset = r.I32(); }
                r.I32(); r.I32(); r.I32(); r.Skip(16); r.U32();
                if (FileVersion >= 365) r.I32();
                if (FileVersion >= 485) r.I32();
                if (FileVersion >= 507) r.Skip(20);
                Exports.Add(e);
            }
        }

        public string FName(BinReader r)
        {
            int idx = r.I32(), num = r.I32();
            string s = idx >= 0 && idx < Names.Count ? Names[idx] : "";
            return num == 0 ? s : s + "_" + (num - 1);
        }

        public string ObjectName(int index)
        {
            if (index < 0 && -index - 1 < Imports.Count) return Imports[-index - 1].ObjectName;
            if (index > 0 && index - 1 < Exports.Count) return Exports[index - 1].Name;
            return null;
        }

        public string ExportClassName(UExport e) => ObjectName(e.ClassIndex) ?? "";

        public UExport FindExport(Func<UExport, bool> predicate)
        {
            foreach (var e in Exports) if (predicate(e)) return e;
            return null;
        }

        /// <summary>Main asset export: the first export that is not a class default object.</summary>
        public UExport MainExport() => FindExport(e => !e.Name.StartsWith("Default__"));

        public Dictionary<string, object> ReadProperties(UExport e)
        {
            if (UnversionedProperties) return new Dictionary<string, object>();
            var r = new BinReader(Data, (int)e.SerialOffset, (int)e.SerialSize);
            return ReadTagged(r, r.Pos + (int)e.SerialSize, 0);
        }

        /// <summary>Byte position right after the tagged properties of an export (for native data).</summary>
        public int NativeDataStart(UExport e)
        {
            var r = new BinReader(Data, (int)e.SerialOffset, (int)e.SerialSize);
            ReadTagged(r, r.Pos + (int)e.SerialSize, 0);
            return r.Pos;
        }

        private static readonly HashSet<string> NativeStructs = new HashSet<string>(StringComparer.Ordinal)
        { "Vector", "Vector2D", "Vector4", "Rotator", "Quat", "LinearColor", "Color", "Guid", "IntPoint", "IntVector", "Box", "Box2D",
          "Transform", "DateTime", "Timespan", "FrameNumber", "SoftObjectPath", "SoftClassPath", "GameplayTagContainer", "PerPlatformFloat", "PerPlatformInt" };

        private Dictionary<string, object> ReadTagged(BinReader r, int end, int depth)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            while (r.Pos + 8 <= end)
            {
                string name = FName(r);
                if (name == "None" || name.Length == 0) break;
                string type = FName(r);
                int size = r.I32();
                int arrayIndex = r.I32();
                string structName = null, inner = null, valueType = null, enumName = null;
                byte boolVal = 0;
                switch (type)
                {
                    case "StructProperty": structName = FName(r); if (FileVersion >= 441) r.Skip(16); break;
                    case "BoolProperty": boolVal = r.U8(); break;
                    case "ByteProperty":
                    case "EnumProperty": enumName = FName(r); break;
                    case "ArrayProperty":
                    case "SetProperty": inner = FName(r); break;
                    case "MapProperty": inner = FName(r); valueType = FName(r); break;
                }
                if (FileVersion >= 503 && r.U8() != 0) r.Skip(16);
                int start = r.Pos;
                object value = null;
                try
                {
                    value = type == "BoolProperty" ? boolVal != 0
                        : ReadValue(r, type, structName, inner, valueType, enumName, size, Math.Min(end, start + size), depth);
                }
                catch { value = null; }
                r.Pos = start + size;
                result[arrayIndex == 0 ? name : name + "[" + arrayIndex + "]"] = value;
            }
            return result;
        }

        private object ReadValue(BinReader r, string type, string structName, string inner, string valueType, string enumName, int size, int end, int depth)
        {
            switch (type)
            {
                case "IntProperty": return r.I32();
                case "UInt32Property": return r.U32();
                case "Int64Property": return r.I64();
                case "FloatProperty": return r.F32();
                case "DoubleProperty": return BitConverter.ToDouble(r.Bytes(8), 0);
                case "StrProperty": return r.FString();
                case "NameProperty": return FName(r);
                case "EnumProperty": return FName(r);
                case "ByteProperty": return enumName != null && enumName != "None" ? (object)FName(r) : r.U8();
                case "TextProperty": return ReadText(r);
                case "ObjectProperty":
                case "ClassProperty": return ObjectName(r.I32());
                case "SoftObjectProperty":
                case "SoftClassProperty": { string p = FName(r); r.FString(); return p; }
                case "StructProperty":
                    if (depth > 12 || structName == null || NativeStructs.Contains(structName)) return null;
                    return ReadTagged(r, end, depth + 1);
                case "ArrayProperty":
                case "SetProperty":
                {
                    if (type == "SetProperty") r.I32();
                    int n = r.I32();
                    var list = new List<object>(Math.Max(0, Math.Min(n, 4096)));
                    if (inner == "StructProperty")
                    {
                        FName(r); FName(r); r.I32(); r.I32();
                        string st = FName(r); r.Skip(16);
                        if (FileVersion >= 503 && r.U8() != 0) r.Skip(16);
                        if (NativeStructs.Contains(st)) return list;
                        for (int i = 0; i < n && r.Pos < end; i++) list.Add(ReadTagged(r, end, depth + 1));
                    }
                    else
                    {
                        for (int i = 0; i < n && r.Pos < end; i++) list.Add(ReadInner(r, inner, end, depth));
                    }
                    return list;
                }
                case "MapProperty":
                {
                    r.I32();
                    int n = r.I32();
                    var map = new List<KeyValuePair<object, object>>();
                    for (int i = 0; i < n && r.Pos < end; i++)
                    {
                        object k = ReadInner(r, inner, end, depth);
                        object v = valueType == "StructProperty" ? ReadTagged(r, end, depth + 1) : ReadInner(r, valueType, end, depth);
                        map.Add(new KeyValuePair<object, object>(k, v));
                    }
                    return map;
                }
            }
            return null;
        }

        private object ReadInner(BinReader r, string type, int end, int depth)
        {
            switch (type)
            {
                case "IntProperty": return r.I32();
                case "FloatProperty": return r.F32();
                case "BoolProperty": return r.U8() != 0;
                case "ByteProperty": return r.U8();
                case "StrProperty": return r.FString();
                case "NameProperty":
                case "EnumProperty": return FName(r);
                case "TextProperty": return ReadText(r);
                case "ObjectProperty":
                case "ClassProperty": return ObjectName(r.I32());
                case "SoftObjectProperty": { string p = FName(r); r.FString(); return p; }
                case "StructProperty": return ReadTagged(r, end, depth + 1);
            }
            throw new NotSupportedException(type);
        }

        private string ReadText(BinReader r)
        {
            r.U32(); // flags
            sbyte history = r.I8();
            switch (history)
            {
                case -1: return r.I32() != 0 ? r.FString() : "";
                case 0: r.FString(); r.FString(); return r.FString();
                case 11: { string table = FName(r); string key = r.FString(); return "LOCTABLE(\"" + table + "\", \"" + key + "\")"; }
                default: return "";
            }
        }

        /// <summary>Reads a UStringTable asset's key/value entries.</summary>
        public Dictionary<string, string> ReadStringTable()
        {
            var e = MainExport() ?? throw new InvalidDataException("No export");
            var r = new BinReader(Data, (int)e.SerialOffset, (int)e.SerialSize);
            ReadTagged(r, r.Pos + (int)e.SerialSize, 0);
            if (r.I32() != 0) r.Skip(16);
            r.FString(); // namespace
            int n = r.I32();
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < n; i++) { string k = r.FString(); d[k] = r.FString(); }
            return d;
        }

        /// <summary>
        /// Extracts the first mip of a cooked Texture2D stored inline (as used by the game's UI
        /// thumbnails). Returns pixel format, size and raw block data, or null.
        /// </summary>
        public (string Format, int Width, int Height, byte[] Pixels)? ReadInlineTexture()
        {
            var e = MainExport();
            if (e == null) return null;
            int start = (int)e.SerialOffset, end = start + (int)e.SerialSize;
            byte[] pf = Encoding.ASCII.GetBytes("PF_");
            for (int p = start + 16; p < end - 16; p++)
            {
                if (Data[p] != pf[0] || Data[p + 1] != pf[1] || Data[p + 2] != pf[2]) continue;
                int len = BitConverter.ToInt32(Data, p - 4);
                if (len < 5 || len > 40) continue;
                string format = Encoding.ASCII.GetString(Data, p, len - 1);
                int width = BitConverter.ToInt32(Data, p - 16), height = BitConverter.ToInt32(Data, p - 12);
                if (width <= 0 || height <= 0 || width > 8192 || height > 8192) continue;
                var r = new BinReader(Data, p + len, end - (p + len));
                r.I32(); // first mip
                int mips = r.I32();
                if (mips <= 0) return null;
                r.I32(); // cooked flag
                uint flags = r.U32();
                long count, sizeOnDisk;
                if ((flags & 0x2000) != 0) { count = r.I64(); sizeOnDisk = r.I64(); } else { count = r.I32(); sizeOnDisk = r.I32(); }
                r.I64(); // offset in file
                if ((flags & 0x40) == 0 || sizeOnDisk <= 0 || sizeOnDisk > r.Remaining) return null; // not inline
                return (format, width, height, r.Bytes((int)sizeOnDisk));
            }
            return null;
        }
    }
}

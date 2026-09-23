using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SandstormModLauncher.Core
{
    public sealed class PakEntry
    {
        public string Path;
        public long Offset;
        public long Size;
        public long UncompressedSize;
        public int CompressionMethod;     // 0 = none, else 1-based index into PakFile.CompressionMethods
        public bool Encrypted;
        public uint CompressionBlockSize;
        public long[] BlockStarts;
        public long[] BlockEnds;
        public int HeaderSize;
    }

    /// <summary>
    /// Read-only Unreal Engine 4 .pak reader (index versions 3-11). Only the index and the
    /// entries asked for are read, so even multi-gigabyte paks open instantly.
    /// </summary>
    public sealed class PakFile : IDisposable
    {
        private const uint Magic = 0x5A6F12E1;
        private readonly FileStream stream;
        private readonly object sync = new object();

        public string FilePath { get; }
        public int Version { get; private set; }
        public string MountPoint { get; private set; } = "";
        public string[] CompressionMethods { get; private set; } = new string[0];
        public bool IndexEncrypted { get; private set; }
        public Dictionary<string, PakEntry> Entries { get; } = new Dictionary<string, PakEntry>(StringComparer.OrdinalIgnoreCase);

        private PakFile(string path)
        {
            FilePath = path;
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);
        }

        public static PakFile Open(string path)
        {
            var pak = new PakFile(path);
            try { pak.ReadIndex(); return pak; }
            catch { pak.Dispose(); throw; }
        }

        public void Dispose() { lock (sync) stream.Dispose(); }

        private byte[] ReadAt(long offset, int count)
        {
            lock (sync)
            {
                stream.Position = offset;
                var buf = new byte[count];
                int read = 0;
                while (read < count)
                {
                    int n = stream.Read(buf, read, count - read);
                    if (n <= 0) throw new EndOfStreamException("Unexpected end of pak " + FilePath);
                    read += n;
                }
                return buf;
            }
        }

        private void ReadIndex()
        {
            long len = stream.Length;
            int tailLen = (int)Math.Min(len, 512);
            byte[] tail = ReadAt(len - tailLen, tailLen);

            // Known footer layouts: magic sits 204 bytes from the end (v8b, v10, v11),
            // 205 (v9 frozen-index flag), 172 (v8a with 4 compression names) or 44 (v1-v7).
            int magicPos = -1;
            foreach (int fromEnd in new[] { 204, 205, 172, 44 })
            {
                int p = tailLen - fromEnd;
                if (p < 0 || p + 28 > tailLen) continue;
                if (BitConverter.ToUInt32(tail, p) != Magic) continue;
                int ver = BitConverter.ToInt32(tail, p + 4);
                if (ver < 1 || ver > 12) continue;
                magicPos = p;
                break;
            }
            if (magicPos < 0) throw new InvalidDataException("Not a recognised Unreal pak file");

            Version = BitConverter.ToInt32(tail, magicPos + 4);
            long indexOffset = BitConverter.ToInt64(tail, magicPos + 8);
            long indexSize = BitConverter.ToInt64(tail, magicPos + 16);
            IndexEncrypted = Version >= 4 && magicPos >= 1 && tail[magicPos - 1] != 0;

            var names = new List<string>();
            if (Version >= 8)
            {
                int namesStart = magicPos + 4 + 4 + 8 + 8 + 20 + (Version == 9 ? 1 : 0);
                int count = (tailLen - namesStart) / 32;
                for (int i = 0; i < count && i < 5; i++)
                {
                    string nm = Encoding.ASCII.GetString(tail, namesStart + i * 32, 32);
                    int z = nm.IndexOf('\0');
                    names.Add(z >= 0 ? nm.Substring(0, z) : nm);
                }
            }
            else names.Add("Zlib");
            CompressionMethods = names.ToArray();

            if (IndexEncrypted) return; // entries cannot be listed without the AES key
            if (indexOffset < 0 || indexSize <= 0 || indexOffset + indexSize > len || indexSize > int.MaxValue)
                throw new InvalidDataException("Invalid pak index location");

            var r = new BinReader(ReadAt(indexOffset, (int)indexSize));
            MountPoint = NormalizeMount(r.FString());
            int numEntries = r.I32();

            if (Version >= 10) ReadPathHashIndex(r, numEntries);
            else
            {
                for (int i = 0; i < numEntries; i++)
                {
                    string name = r.FString();
                    var e = ReadLegacyEntry(r);
                    Add(name, e);
                }
            }
        }

        private static string NormalizeMount(string mount)
        {
            mount = (mount ?? "").Replace('\\', '/');
            while (mount.StartsWith("../")) mount = mount.Substring(3);
            if (mount.StartsWith("/")) mount = mount.Substring(1);
            if (mount.Length > 0 && !mount.EndsWith("/")) mount += "/";
            return mount;
        }

        private void Add(string relative, PakEntry e)
        {
            relative = relative.Replace('\\', '/').TrimStart('/');
            string full = MountPoint + relative;
            e.Path = full;
            Entries[full] = e;
        }

        private PakEntry ReadLegacyEntry(BinReader r)
        {
            var e = new PakEntry { Offset = r.I64(), Size = r.I64(), UncompressedSize = r.I64() };
            if (Version >= 8)
            {
                e.CompressionMethod = (Version == 8 && CompressionMethods.Length == 4) ? r.U8() : (int)r.U32();
            }
            else
            {
                int legacy = r.I32();
                e.CompressionMethod = (legacy & 0x01) != 0 ? 1 : (legacy == 0 ? 0 : 99);
            }
            if (Version <= 1) r.I64(); // timestamp
            r.Skip(20);
            int blocks = 0;
            if (Version >= 3)
            {
                if (e.CompressionMethod != 0)
                {
                    blocks = r.I32();
                    e.BlockStarts = new long[blocks];
                    e.BlockEnds = new long[blocks];
                    for (int b = 0; b < blocks; b++) { e.BlockStarts[b] = r.I64(); e.BlockEnds[b] = r.I64(); }
                }
                e.Encrypted = (r.U8() & 1) != 0;
                e.CompressionBlockSize = r.U32();
            }
            e.HeaderSize = SerializedHeaderSize(e.CompressionMethod != 0, blocks);
            return e;
        }

        private int SerializedHeaderSize(bool compressed, int blocks)
        {
            int size = 8 + 8 + 8 + 20 + 4;
            if (Version >= 3) size += 1 + 4 + (compressed ? 4 + 16 * blocks : 0);
            if (Version <= 1) size += 8;
            return size;
        }

        private void ReadPathHashIndex(BinReader r, int numEntries)
        {
            r.U64(); // path hash seed
            if (r.I32() != 0) { r.I64(); r.I64(); r.Skip(20); }
            bool hasFullDirectory = r.I32() != 0;
            long fullOffset = 0, fullSize = 0;
            if (hasFullDirectory) { fullOffset = r.I64(); fullSize = r.I64(); r.Skip(20); }

            int encodedSize = r.I32();
            byte[] encoded = r.Bytes(encodedSize);
            int nonEncodedCount = r.I32();
            var nonEncoded = new List<PakEntry>(nonEncodedCount);
            for (int i = 0; i < nonEncodedCount; i++) nonEncoded.Add(ReadLegacyEntry(r));

            if (!hasFullDirectory || fullSize <= 0 || fullSize > int.MaxValue) return; // names not recoverable
            var d = new BinReader(ReadAt(fullOffset, (int)fullSize));
            int dirCount = d.I32();
            for (int i = 0; i < dirCount; i++)
            {
                string dir = d.FString();
                int fileCount = d.I32();
                for (int f = 0; f < fileCount; f++)
                {
                    string file = d.FString();
                    int loc = d.I32();
                    PakEntry e;
                    if (loc >= 0) e = DecodeEntry(encoded, loc);
                    else
                    {
                        int idx = -loc - 1;
                        if (idx >= nonEncoded.Count) continue;
                        var src = nonEncoded[idx];
                        e = new PakEntry
                        {
                            Offset = src.Offset, Size = src.Size, UncompressedSize = src.UncompressedSize,
                            CompressionMethod = src.CompressionMethod, Encrypted = src.Encrypted,
                            CompressionBlockSize = src.CompressionBlockSize, BlockStarts = src.BlockStarts,
                            BlockEnds = src.BlockEnds, HeaderSize = src.HeaderSize
                        };
                    }
                    Add(dir + file, e);
                }
            }
        }

        private PakEntry DecodeEntry(byte[] enc, int p)
        {
            uint v = BitConverter.ToUInt32(enc, p); p += 4;
            uint blockSize;
            if ((v & 0x3f) == 0x3f) { blockSize = BitConverter.ToUInt32(enc, p); p += 4; }
            else blockSize = (v & 0x3f) << 11;

            var e = new PakEntry { CompressionMethod = (int)((v >> 23) & 0x3f) };
            if ((v & (1u << 31)) != 0) { e.Offset = BitConverter.ToUInt32(enc, p); p += 4; }
            else { e.Offset = BitConverter.ToInt64(enc, p); p += 8; }
            if ((v & (1u << 30)) != 0) { e.UncompressedSize = BitConverter.ToUInt32(enc, p); p += 4; }
            else { e.UncompressedSize = BitConverter.ToInt64(enc, p); p += 8; }
            if (e.CompressionMethod != 0)
            {
                if ((v & (1u << 29)) != 0) { e.Size = BitConverter.ToUInt32(enc, p); p += 4; }
                else { e.Size = BitConverter.ToInt64(enc, p); p += 8; }
            }
            else e.Size = e.UncompressedSize;

            e.Encrypted = (v & (1u << 22)) != 0;
            int blocks = (int)((v >> 6) & 0xffff);
            e.CompressionBlockSize = blocks == 1 ? (uint)e.UncompressedSize : blockSize;
            e.HeaderSize = SerializedHeaderSize(e.CompressionMethod != 0, blocks);
            if (blocks > 0)
            {
                e.BlockStarts = new long[blocks];
                e.BlockEnds = new long[blocks];
                if (blocks == 1 && !e.Encrypted)
                {
                    e.BlockStarts[0] = e.HeaderSize;
                    e.BlockEnds[0] = e.HeaderSize + e.Size;
                }
                else
                {
                    long off = e.HeaderSize;
                    for (int b = 0; b < blocks; b++)
                    {
                        uint bs = BitConverter.ToUInt32(enc, p); p += 4;
                        e.BlockStarts[b] = off;
                        e.BlockEnds[b] = off + bs;
                        off += e.Encrypted ? ((bs + 15) & ~15u) : bs;
                    }
                }
            }
            return e;
        }

        public string CompressionName(PakEntry e)
        {
            if (e.CompressionMethod == 0) return "None";
            int i = e.CompressionMethod - 1;
            return i >= 0 && i < CompressionMethods.Length ? CompressionMethods[i] : "Unknown";
        }

        public bool CanRead(PakEntry e)
        {
            if (e == null || e.Encrypted) return false;
            if (e.CompressionMethod == 0) return true;
            string c = CompressionName(e);
            return c.Equals("Zlib", StringComparison.OrdinalIgnoreCase) || c.Equals("Gzip", StringComparison.OrdinalIgnoreCase);
        }

        public bool TryRead(string path, out byte[] data)
        {
            data = null;
            if (!Entries.TryGetValue(path, out var e) || !CanRead(e)) return false;
            try { data = Read(e); return true; }
            catch { return false; }
        }

        public byte[] Read(PakEntry e)
        {
            if (e.Encrypted) throw new NotSupportedException("Entry is encrypted");
            if (e.UncompressedSize > 512L * 1024 * 1024) throw new NotSupportedException("Entry too large");
            if (e.CompressionMethod == 0)
                return ReadAt(e.Offset + e.HeaderSize, (int)e.UncompressedSize);

            string method = CompressionName(e);
            bool gzip = method.Equals("Gzip", StringComparison.OrdinalIgnoreCase);
            if (!gzip && !method.Equals("Zlib", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Compression " + method + " is not supported");

            var output = new byte[e.UncompressedSize];
            int written = 0;
            bool relative = Version >= 5;
            for (int b = 0; b < e.BlockStarts.Length; b++)
            {
                long start = (relative ? e.Offset : 0) + e.BlockStarts[b];
                int clen = (int)(e.BlockEnds[b] - e.BlockStarts[b]);
                byte[] comp = ReadAt(start, clen);
                int expected = (int)Math.Min(e.CompressionBlockSize == 0 ? e.UncompressedSize : e.CompressionBlockSize, e.UncompressedSize - written);
                using (var ms = new MemoryStream(comp, gzip ? 0 : 2, gzip ? clen : clen - 2))
                using (Stream z = gzip ? (Stream)new GZipStream(ms, CompressionMode.Decompress) : new DeflateStream(ms, CompressionMode.Decompress))
                {
                    int got = 0;
                    while (got < expected)
                    {
                        int n = z.Read(output, written + got, expected - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    written += got;
                }
            }
            return output;
        }
    }
}

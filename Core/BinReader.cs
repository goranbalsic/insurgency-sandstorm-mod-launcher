using System;
using System.IO;
using System.Text;

namespace SandstormModLauncher.Core
{
    /// <summary>Little-endian reader over a byte array with Unreal FString support.</summary>
    public sealed class BinReader
    {
        public readonly byte[] Data;
        public int Pos;
        private readonly int end;

        public BinReader(byte[] data, int pos = 0, int length = -1)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
            Pos = pos;
            end = length < 0 ? data.Length : Math.Min(data.Length, pos + length);
        }

        public int Length => end;
        public int Remaining => end - Pos;

        private void Need(int n)
        {
            if (n < 0 || Pos + n > end) throw new InvalidDataException($"Read past end of buffer (pos {Pos}, need {n}, end {end})");
        }

        public byte U8() { Need(1); return Data[Pos++]; }
        public sbyte I8() { Need(1); return (sbyte)Data[Pos++]; }
        public short I16() { Need(2); short v = BitConverter.ToInt16(Data, Pos); Pos += 2; return v; }
        public ushort U16() { Need(2); ushort v = BitConverter.ToUInt16(Data, Pos); Pos += 2; return v; }
        public int I32() { Need(4); int v = BitConverter.ToInt32(Data, Pos); Pos += 4; return v; }
        public uint U32() { Need(4); uint v = BitConverter.ToUInt32(Data, Pos); Pos += 4; return v; }
        public long I64() { Need(8); long v = BitConverter.ToInt64(Data, Pos); Pos += 8; return v; }
        public ulong U64() { Need(8); ulong v = BitConverter.ToUInt64(Data, Pos); Pos += 8; return v; }
        public float F32() { Need(4); float v = BitConverter.ToSingle(Data, Pos); Pos += 4; return v; }

        public void Skip(int n) { Need(n); Pos += n; }

        public byte[] Bytes(int n)
        {
            Need(n);
            var b = new byte[n];
            Buffer.BlockCopy(Data, Pos, b, 0, n);
            Pos += n;
            return b;
        }

        public Guid Guid() => new Guid(Bytes(16));

        /// <summary>Unreal FString: int32 length (negative = UTF-16), null terminated.</summary>
        public string FString()
        {
            int n = I32();
            if (n == 0) return string.Empty;
            string s;
            if (n < 0)
            {
                if (n == int.MinValue || -n > 1_000_000) throw new InvalidDataException("Bad FString length " + n);
                int bytes = -n * 2;
                Need(bytes);
                s = Encoding.Unicode.GetString(Data, Pos, bytes);
                Pos += bytes;
            }
            else
            {
                if (n > 4_000_000) throw new InvalidDataException("Bad FString length " + n);
                Need(n);
                s = Latin1.GetString(Data, Pos, n);
                Pos += n;
            }
            int z = s.IndexOf('\0');
            return z >= 0 ? s.Substring(0, z) : s;
        }

        public static readonly Encoding Latin1 = Encoding.GetEncoding(28591);
    }
}

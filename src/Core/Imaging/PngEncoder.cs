using System.IO.Compression;

namespace MicroFocus.Core.Imaging;

/// <summary>Minimal PNG writer (grayscale 8-bit and RGB) using System.IO.Compression, no external dependencies.</summary>
public static class PngEncoder
{
    public static byte[] Grayscale(byte[] gray, int width, int height)
    {
        var raw = new byte[checked((width + 1) * (long)height)];
        for (int y = 0; y < height; y++)
        {
            raw[(long)y * (width + 1)] = 0;
            Buffer.BlockCopy(gray, y * width, raw, y * (width + 1) + 1, width);
        }
        return BuildPng(width, height, 8, 0, raw);
    }

    public static byte[] Rgb(byte[] rgb, int width, int height)
    {
        var raw = new byte[checked((width * 3 + 1) * (long)height)];
        for (int y = 0; y < height; y++)
        {
            raw[(long)y * (width * 3 + 1)] = 0;
            Buffer.BlockCopy(rgb, y * width * 3, raw, y * (width * 3 + 1) + 1, width * 3);
        }
        return BuildPng(width, height, 8, 2, raw);
    }

    private static byte[] BuildPng(int w, int h, int bitDepth, int colorType, byte[] raw)
    {
        if (raw.LongLength > int.MaxValue) throw new NotSupportedException("预览图过大");
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 });
        WriteChunk(ms, "IHDR", Bytes(new uint[] { (uint)w, (uint)h }, new byte[] { (byte)bitDepth, (byte)colorType, 0, 0, 0 }));
        WriteChunk(ms, "IDAT", Zlib(raw));
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static byte[] Bytes(uint[] ints, byte[] tail)
    {
        var list = new List<byte>(ints.Length * 4 + tail.Length);
        foreach (var v in ints)
        {
            list.Add((byte)(v >> 24));
            list.Add((byte)(v >> 16));
            list.Add((byte)(v >> 8));
            list.Add((byte)v);
        }
        list.AddRange(tail);
        return list.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        void Write4(uint v) => s.Write(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
        Write4((uint)data.Length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc32(typeBytes, data);
        Write4(crc);
    }

    private static byte[] Zlib(byte[] data)
    {
        using var outMs = new MemoryStream();
        outMs.WriteByte(0x78);
        outMs.WriteByte(0x01);
        using (var ds = new DeflateStream(outMs, CompressionLevel.Fastest, leaveOpen: true))
            ds.Write(data, 0, (int)data.LongLength);
        uint a1 = Adler32(data);
        outMs.WriteByte((byte)(a1 >> 24));
        outMs.WriteByte((byte)(a1 >> 16));
        outMs.WriteByte((byte)(a1 >> 8));
        outMs.WriteByte((byte)a1);
        return outMs.ToArray();
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var x in data) { a = (a + x) % 65521; b = (b + a) % 65521; }
        return (b << 16) | a;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();
    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(byte[] a, byte[] b)
    {
        uint c = 0xFFFFFFFF;
        foreach (var x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}

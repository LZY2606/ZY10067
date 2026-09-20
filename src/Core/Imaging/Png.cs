using System.IO.Compression;

namespace MicroStack.Core.Imaging;

/// <summary>8-bit grayscale or RGB image. ChannelCount is 1 or 3; pixel-interleaved rows.</summary>
public sealed class PngImage
{
    public int Width { get; }
    public int Height { get; }
    public int Channels { get; }
    public byte[] Pixels { get; }

    public PngImage(int width, int height, int channels, byte[]? pixels = null)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("image dimensions must be positive");
        if (channels is not (1 or 3)) throw new ArgumentException("only 8-bit grayscale or RGB is supported");
        Width = width;
        Height = height;
        Channels = channels;
        Pixels = pixels ?? new byte[width * height * channels];
        if (Pixels.Length != width * height * channels) throw new ArgumentException("pixel buffer size mismatch");
    }

    public byte GrayAt(int x, int y)
    {
        int i = (y * Width + x) * Channels;
        if (Channels == 1) return Pixels[i];
        return (byte)((Pixels[i] * 299 + Pixels[i + 1] * 587 + Pixels[i + 2] * 114 + 500) / 1000);
    }

    public void SetGray(int x, int y, byte v)
    {
        int i = (y * Width + x) * Channels;
        if (Channels == 1) { Pixels[i] = v; return; }
        Pixels[i] = Pixels[i + 1] = Pixels[i + 2] = v;
    }
}

/// <summary>Minimal offline PNG codec: 8-bit, color types 0/2/6, filter types 0-4, no interlace.</summary>
public static class Png
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static PngImage Decode(byte[] data)
    {
        if (data.Length < 8 + 12 || !Signature.SequenceEqual(data.Take(8)))
            throw new InvalidDataException("not a PNG file (bad signature)");
        int pos = 8;
        int width = 0, height = 0, bitDepth = 0, colorType = -1;
        List<byte[]>? idat = null;
        byte[]? palette = null;
        byte[]? trns = null;
        bool seenEnd = false;
        while (pos + 8 <= data.Length)
        {
            int len = ReadInt32(data, pos);
            string type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            int dataStart = pos + 8;
            if (dataStart + len + 4 > data.Length) throw new InvalidDataException("truncated PNG chunk");
            if (type == "IHDR")
            {
                if (len != 13) throw new InvalidDataException("bad IHDR length");
                width = ReadInt32(data, dataStart);
                height = ReadInt32(data, dataStart + 4);
                bitDepth = data[dataStart + 8];
                colorType = data[dataStart + 9];
                if (data[dataStart + 10] != 0) throw new InvalidDataException("unsupported PNG compression method");
                if (data[dataStart + 11] != 0) throw new InvalidDataException("unsupported PNG filter method");
                if (data[dataStart + 12] != 0) throw new InvalidDataException("interlaced PNG is not supported");
            }
            else if (type == "PLTE")
            {
                palette = data[dataStart..(dataStart + len)];
            }
            else if (type == "tRNS")
            {
                trns = data[dataStart..(dataStart + len)];
            }
            else if (type == "IDAT")
            {
                byte[] part = new byte[len];
                Buffer.BlockCopy(data, dataStart, part, 0, len);
                (idat ??= []).Add(part);
            }
            else if (type == "IEND")
            {
                seenEnd = true;
                break;
            }
            pos = dataStart + len + 4;
        }
        if (!seenEnd) throw new InvalidDataException("PNG missing IEND");
        if (width <= 0 || height <= 0) throw new InvalidDataException("bad PNG dimensions");
        if (bitDepth != 8) throw new InvalidDataException($"unsupported PNG bit depth {bitDepth} (only 8)");

        int channels;
        switch (colorType)
        {
            case 0: channels = 1; break;
            case 2: channels = 3; break;
            case 6: channels = 4; break;
            case 3:
                channels = 3;
                if (palette == null) throw new InvalidDataException("indexed PNG missing PLTE");
                break;
            default: throw new InvalidDataException($"unsupported PNG color type {colorType}");
        }

        using var ms = new MemoryStream();
        if (idat != null) foreach (var chunk in idat) ms.Write(chunk, 0, chunk.Length);
        ms.Position = 0;
        using var zlib = new ZLibStream(ms, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        byte[] filtered = raw.ToArray();

        int stride = width * channels;
        long expected = (long)height * (stride + 1);
        if (filtered.Length < expected) throw new InvalidDataException("truncated PNG image data");

        byte[] outPixels = new byte[width * height * (colorType == 6 ? 3 : channels)];
        byte[] prev = new byte[stride];
        byte[] cur = new byte[stride];
        int bpp = channels;
        int src = 0;
        for (int y = 0; y < height; y++)
        {
            byte filter = filtered[src++];
            Buffer.BlockCopy(filtered, src, cur, 0, stride);
            src += stride;
            switch (filter)
            {
                case 0: break;
                case 1:
                    for (int i = bpp; i < stride; i++) cur[i] = (byte)(cur[i] + cur[i - bpp]);
                    break;
                case 2:
                    for (int i = 0; i < stride; i++) cur[i] = (byte)(cur[i] + prev[i]);
                    break;
                case 3:
                    for (int i = 0; i < stride; i++)
                    {
                        int a = i >= bpp ? cur[i - bpp] : 0;
                        cur[i] = (byte)(cur[i] + ((a + prev[i]) >> 1));
                    }
                    break;
                case 4:
                    for (int i = 0; i < stride; i++)
                    {
                        int a = i >= bpp ? cur[i - bpp] : 0;
                        int b = prev[i];
                        int c = i >= bpp ? prev[i - bpp] : 0;
                        int p = a + b - c;
                        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                        int pred = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                        cur[i] = (byte)(cur[i] + pred);
                    }
                    break;
                default: throw new InvalidDataException($"unsupported PNG filter {filter}");
            }

            if (colorType == 3)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = cur[x];
                    if (idx * 3 + 2 >= palette!.Length) throw new InvalidDataException("PNG palette index out of range");
                    int oi = (y * width + x) * 3;
                    outPixels[oi] = palette[idx * 3];
                    outPixels[oi + 1] = palette[idx * 3 + 1];
                    outPixels[oi + 2] = palette[idx * 3 + 2];
                }
            }
            else if (colorType == 6)
            {
                for (int x = 0; x < width; x++)
                {
                    int si = x * 4, oi = (y * width + x) * 3;
                    byte alpha = cur[si + 3];
                    if (alpha == 0) { outPixels[oi] = outPixels[oi + 1] = outPixels[oi + 2] = 0; }
                    else
                    {
                        outPixels[oi] = cur[si];
                        outPixels[oi + 1] = cur[si + 1];
                        outPixels[oi + 2] = cur[si + 2];
                    }
                }
            }
            else
            {
                Buffer.BlockCopy(cur, 0, outPixels, y * stride, stride);
            }
            (cur, prev) = (prev, cur);
        }
        int outChannels = colorType == 6 ? 3 : channels;
        return new PngImage(width, height, outChannels, outPixels);
    }

    public static byte[] Encode(PngImage image) => Encode(image.Pixels, image.Width, image.Height, image.Channels);

    public static byte[] Encode(byte[] pixels, int width, int height, int channels)
    {
        int stride = width * channels;
        if (pixels.Length != stride * height) throw new ArgumentException("pixel buffer size mismatch");
        using var raw = new MemoryStream();
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            raw.Write(pixels, y * stride, stride);
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            raw.WriteTo(zlib);
        byte[] idat = compressed.ToArray();

        using var ms = new MemoryStream();
        ms.Write(Signature, 0, 8);
        WriteChunk(ms, "IHDR", BuildIhdr(width, height, channels));
        WriteChunk(ms, "IDAT", idat);
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static byte[] BuildIhdr(int width, int height, int channels)
    {
        byte[] b = new byte[13];
        WriteInt32(b, 0, width);
        WriteInt32(b, 4, height);
        b[8] = 8;
        b[9] = (byte)(channels switch { 1 => 0, 3 => 2, _ => throw new ArgumentException("bad channel count") });
        return b;
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        byte[] len = new byte[4];
        WriteInt32(len, 0, data.Length);
        s.Write(len, 0, 4);
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes, 0, 4);
        s.Write(data, 0, data.Length);
        byte[] crcInput = new byte[4 + data.Length];
        Buffer.BlockCopy(typeBytes, 0, crcInput, 0, 4);
        Buffer.BlockCopy(data, 0, crcInput, 4, data.Length);
        byte[] crc = new byte[4];
        WriteUInt32(crc, 0, Crc32.Compute(crcInput));
        s.Write(crc, 0, 4);
    }

    private static int ReadInt32(byte[] b, int o) =>
        (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

    private static void WriteInt32(byte[] b, int o, int v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }

    private static void WriteUInt32(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }
}

internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data) crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
}

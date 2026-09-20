using System.Text;

namespace MicroFocus.Core.Imaging;

/// <summary>Portable anymap codec: binary P5 (PGM grayscale) and P6 (PPM RGB), ASCII P2/P3 for test input.</summary>
public static class PnmCodec
{
    public sealed class PnmImage
    {
        public required int Width { get; init; }
        public required int Height { get; init; }
        /// <summary>1 for PGM, 3 for PPM.</summary>
        public required int Channels { get; init; }
        public required int MaxValue { get; init; }
        /// <summary>Planar-interleaved grayscale data normalized to 0..255 (channel-averaged for RGB).</summary>
        public required byte[] Gray { get; init; }
        /// <summary>Null for grayscale; interleaved RGB otherwise.</summary>
        public byte[]? Rgb { get; init; }
    }

    public static PnmImage Decode(ReadOnlySpan<byte> data)
    {
        int pos = 0;
        string magic = ReadToken(data, ref pos) ?? throw new InvalidDataException("missing PNM magic");
        if (magic is not ("P2" or "P3" or "P5" or "P6"))
            throw new InvalidDataException($"unsupported PNM magic '{magic}' (only P2/P3/P5/P6 supported)");
        int w = int.Parse(ReadToken(data, ref pos) ?? throw new InvalidDataException("missing width"));
        int h = int.Parse(ReadToken(data, ref pos) ?? throw new InvalidDataException("missing height"));
        int max = int.Parse(ReadToken(data, ref pos) ?? throw new InvalidDataException("missing max value"));
        if (w <= 0 || h <= 0 || max <= 0 || max > 65535)
            throw new InvalidDataException("invalid PNM dimensions or max value");

        bool ascii = magic is "P2" or "P3";
        int channels = magic is "P3" or "P6" ? 3 : 1;
        if (pos >= data.Length)
            throw new InvalidDataException("truncated PNM raster");
        byte sep = data[pos++];
        if (sep is not ((byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t'))
            throw new InvalidDataException("expected single whitespace after PNM header");

        long pixels = (long)w * h;
        byte[] gray = new byte[pixels];
        byte[]? rgb = channels == 3 ? new byte[pixels * 3] : null;

        if (ascii)
        {
            int samples = 0;
            long totalSamples = pixels * channels;
            int idx = 0;
            while (samples < totalSamples)
            {
                var token = ReadToken(data, ref pos);
                if (token == null) throw new InvalidDataException("truncated ASCII PNM raster");
                int v = Scale(int.Parse(token), max);
                if (channels == 1) gray[samples] = (byte)v;
                else
                {
                    rgb![idx++] = (byte)v;
                    int s = samples % 3;
                    if (s == 2)
                    {
                        int pi = samples / 3;
                        gray[pi] = (byte)((rgb[(long)pi * 3] + rgb[(long)pi * 3 + 1] + rgb[(long)pi * 3 + 2]) / 3);
                    }
                }
                samples++;
            }
        }
        else
        {
            bool wide = max > 255;
            long needed = pixels * channels * (wide ? 2 : 1);
            if ((long)pos + needed > data.Length) throw new InvalidDataException("truncated binary PNM raster");
            for (long p = 0; p < pixels; p++)
            {
                if (channels == 1)
                {
                    int raw = wide ? (data[pos++] << 8 | data[pos++]) : data[pos++];
                    gray[p] = (byte)Scale(raw, max);
                }
                else
                {
                    int r = Scale(wide ? (data[pos++] << 8 | data[pos++]) : data[pos++], max);
                    int g = Scale(wide ? (data[pos++] << 8 | data[pos++]) : data[pos++], max);
                    int b = Scale(wide ? (data[pos++] << 8 | data[pos++]) : data[pos++], max);
                    rgb![p * 3] = (byte)r;
                    rgb[p * 3 + 1] = (byte)g;
                    rgb[p * 3 + 2] = (byte)b;
                    gray[p] = (byte)((r + g + b) / 3);
                }
            }
        }
        return new PnmImage { Width = w, Height = h, Channels = channels, MaxValue = max, Gray = gray, Rgb = rgb };
    }

    public static byte[] EncodeP5(ReadOnlySpan<byte> gray, int width, int height)
    {
        if (gray.Length != (long)width * height) throw new ArgumentException("raster size mismatch");
        using var ms = new MemoryStream(gray.Length + 64);
        var header = Encoding.ASCII.GetBytes($"P5\n{width} {height}\n255\n");
        ms.Write(header);
        ms.Write(gray);
        return ms.ToArray();
    }

    private static int Scale(int value, int max) => max == 255 ? value : (int)Math.Round(value * 255.0 / max);

    private static string? ReadToken(ReadOnlySpan<byte> data, ref int pos)
    {
        while (pos < data.Length)
        {
            byte c = data[pos];
            if (c == '#')
            {
                while (pos < data.Length && data[pos] != '\n') pos++;
                continue;
            }
            if (c is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t')
            {
                pos++;
                continue;
            }
            break;
        }
        int start = pos;
        while (pos < data.Length && data[pos] is not ((byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t'))
        {
            if (data[pos] == '#') break;
            pos++;
        }
        return pos > start ? Encoding.ASCII.GetString(data[start..pos]) : null;
    }
}

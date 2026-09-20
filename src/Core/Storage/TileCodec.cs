namespace MicroFocus.Core.Storage;

/// <summary>Tile derivative binary: big-endian header + gray raster + uint16 source index + byte trust map.</summary>
public static class TileCodec
{
    public sealed class DecodedTile
    {
        public required int X { get; init; }
        public required int Y { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required byte[] Gray { get; init; }
        public required ushort[] SourceIndex { get; init; }
        public required byte[] Trust { get; init; }
    }

    public static byte[] Encode(int x, int y, int w, int h, byte[] gray, ushort[] source, byte[] trust)
    {
        int n = w * h;
        using var ms = new MemoryStream(16 + n * 4);
        using var bw = new BinaryWriter(ms);
        bw.Write((uint)x);
        bw.Write((uint)y);
        bw.Write((uint)w);
        bw.Write((uint)h);
        bw.Write(gray);
        for (int i = 0; i < n; i++)
        {
            ushort v = source[i];
            bw.Write((byte)(v >> 8));
            bw.Write((byte)(v & 0xFF));
        }
        bw.Write(trust);
        return ms.ToArray();
    }

    public static DecodedTile Decode(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var br = new BinaryReader(ms);
        int x = (int)br.ReadUInt32(), y = (int)br.ReadUInt32();
        int w = (int)br.ReadUInt32(), h = (int)br.ReadUInt32();
        int n = w * h;
        var gray = br.ReadBytes(n);
        var source = new ushort[n];
        for (int i = 0; i < n; i++)
            source[i] = (ushort)((br.ReadByte() << 8) | br.ReadByte());
        var trust = br.ReadBytes(n);
        return new DecodedTile { X = x, Y = y, Width = w, Height = h, Gray = gray, SourceIndex = source, Trust = trust };
    }
}

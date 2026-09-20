namespace MicroFocus.Core.Services;

/// <summary>Assembles tile rasters into full-canvas derivative artifacts.</summary>
public static class VersionDerivatives
{
    public sealed class CanvasRasters
    {
        public required byte[] Gray { get; init; }
        public required ushort[] SourceIndex { get; init; }
        public required byte[] Trust { get; init; }
    }

    public static CanvasRasters Assemble(IEnumerable<TileArtifact> tiles, int width, int height)
    {
        var gray = new byte[width * (long)height];
        var source = new ushort[width * (long)height];
        var trust = new byte[width * (long)height];
        Array.Fill(source, (ushort)0);
        Array.Fill(trust, (byte)TrustCode.NoCoverage);

        foreach (var tile in tiles)
        {
            for (int y = 0; y < tile.Height; y++)
            {
                for (int x = 0; x < tile.Width; x++)
                {
                    int ti = y * tile.Width + x;
                    long ci = (long)(tile.Y + y) * width + (tile.X + x);
                    gray[ci] = tile.Gray[ti];
                    source[ci] = tile.SourceIndex[ti];
                    trust[ci] = tile.Trust[ti];
                }
            }
        }
        return new CanvasRasters { Gray = gray, SourceIndex = source, Trust = trust };
    }
}

public sealed class TileArtifact
{
    public required int Index { get; set; }
    public required int X { get; set; }
    public required int Y { get; set; }
    public required int Width { get; set; }
    public required int Height { get; set; }
    public required byte[] Gray { get; set; }
    public required ushort[] SourceIndex { get; set; }
    public required byte[] Trust { get; set; }
}

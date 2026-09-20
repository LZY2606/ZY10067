using MicroStack.Core.Models;

namespace MicroStack.Core.Engine;

/// <summary>Pixel-space placement of an input frame on the output canvas.</summary>
public sealed class FrameLayout
{
    public required string EvidenceId { get; init; }
    public required FrameRef Frame { get; init; }
    public double PixelSizeUm { get; init; }
    /// <summary>Canvas pixel coordinate of frame pixel (0,0).</summary>
    public double OriginX { get; set; }
    public double OriginY { get; set; }
    public int DrawX0 { get; set; }
    public int DrawY0 { get; set; }
    public int DrawX1 { get; set; }
    public int DrawY1 { get; set; }
    public bool Placed { get; init; }
    public string? Reason { get; init; }
    public bool MagnificationConflict { get; init; }

    public void Shift(int dx, int dy)
    {
        OriginX += dx; OriginY += dy;
        DrawX0 += dx; DrawY0 += dy; DrawX1 += dx; DrawY1 += dy;
    }
}

namespace MicroStack.Core.Models;

/// <summary>Algorithm knobs that must travel with every produced result.</summary>
public sealed class EngineParameters
{
    /// <summary>Logical tile edge in output pixels.</summary>
    public int TileSize { get; set; } = 64;

    /// <summary>Half-window radius for local sharpness averaging.</summary>
    public int SharpnessWindowRadius { get; set; } = 3;

    /// <summary>A covered pixel within this many pixels of a source frame border is treated as extrapolated edge.</summary>
    public int EdgeGuardPixels { get; set; } = 2;

    /// <summary>Minimum number of aligned usable frames covering a pixel below which it is flagged InsufficientAlignment.</summary>
    public int MinimumAlignedFrames { get; set; } = 1;

    /// <summary>Micrometers per pixel at magnification 1x; actual pixel size = ScaleFactor / magnification.</summary>
    public double PixelScaleFactorUm { get; set; } = 10.0;

    /// <summary>Magnification values closer than this relative amount are considered equal.</summary>
    public double MagnificationTolerance { get; set; } = 0.02;
}

public static class RuleSet
{
    /// <summary>Bumped whenever scoring/selection/geometry semantics change.</summary>
    public const string Version = "microstack-rules-1.0.0";
}

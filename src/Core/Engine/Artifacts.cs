using MicroStack.Core.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MicroStack.Core.Engine;

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static readonly JsonSerializerOptions Web = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed class FrameTransform
{
    public int FrameIndex { get; set; }
    public string EvidenceId { get; set; } = "";
    public string ContentSha256 { get; set; } = "";
    public bool Placed { get; set; }
    public string? Reason { get; set; }
    public bool MagnificationConflict { get; set; }
    public double PixelSizeUm { get; set; }
    public double OriginCanvasX { get; set; }
    public double OriginCanvasY { get; set; }
    /// <summary>Number of source pixels per canvas pixel (mapping scale along x/y).</summary>
    public double SourcePixelsPerCanvasPixel { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool MagnificationMissing { get; set; }
    public double Magnification { get; set; }
    public double? StageXUm { get; set; }
    public double? StageYUm { get; set; }
    public string? StageXUnit { get; set; }
    public string? StageYUnit { get; set; }
    public double ZHeightUm { get; set; }
    public string? CapturedAt { get; set; }
}

public sealed class TransformDoc
{
    public string RuleSetVersion { get; set; } = "";
    public int CanvasWidth { get; set; }
    public int CanvasHeight { get; set; }
    public double ReferenceMagnification { get; set; }
    public double ReferencePixelSizeUm { get; set; }
    public EngineParameters Parameters { get; set; } = new();
    public List<FrameTransform> Frames { get; set; } = [];
}

public sealed class FrameScore
{
    public string EvidenceId { get; set; } = "";
    public double MeanSharpness { get; set; }
}

public sealed class TileScores
{
    public int TileIndex { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Validated { get; set; }
    public string? ForcedEvidenceId { get; set; }
    public List<string> ExcludedEvidenceIds { get; set; } = [];
    public string? SelectedEvidenceId { get; set; }
    public int TrustedPixels { get; set; }
    public Dictionary<string, int> UntrustedPixels { get; set; } = [];
    public List<FrameScore> FrameScores { get; set; } = [];
}

public sealed class ScoresDoc
{
    public string RuleSetVersion { get; set; } = "";
    public EngineParameters Parameters { get; set; } = new();
    public double ReferenceMagnification { get; set; }
    public List<TileScores> Tiles { get; set; } = [];
}

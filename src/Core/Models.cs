using System.Text.Json.Serialization;

namespace MicroFocus.Core;

public enum TrustCode : byte
{
    Trusted = 0,
    SingleFrame = 1,
    ScaleMissing = 2,
    MagnificationConflict = 3,
    NoCoverage = 4,
    ManualOverride = 5,
}

public enum FrameRole
{
    Primary,
    Reshoot,
}

public sealed class FrameRecord
{
    public required string FrameId { get; set; }
    public required string JobId { get; set; }
    public required string OriginalFileName { get; set; }
    public required string Sha256 { get; set; }
    public long SizeBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Channels { get; set; }
    public int MaxValue { get; set; }

    public double? Magnification { get; set; }
    public bool MagnificationInferred { get; set; }
    public double? PixelSizeUm { get; set; }
    public bool PixelSizeInferred { get; set; }
    public string? StageUnit { get; set; }
    public double? StageXUm { get; set; }
    public double? StageYUm { get; set; }
    public bool StageCoordinatesTrusted { get; set; }
    public double? ZUm { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public FrameRole Role { get; set; } = FrameRole.Primary;
    public string? DustNote { get; set; }
}

public sealed class JobRecord
{
    public required string JobId { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<string> FrameIds { get; set; } = new();
}

public sealed class AlignmentDiagnostic
{
    public required string Code { get; set; }
    public required string Severity { get; set; }
    public string? FrameId { get; set; }
    public required string Message { get; set; }
}

public sealed class FrameTransformInfo
{
    public required string FrameId { get; set; }
    public required string Sha256 { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double PixelSizeUm { get; set; }
    public double? Magnification { get; set; }
    public bool MagnificationInferred { get; set; }
    public bool PixelSizeInferred { get; set; }
    public bool StageCoordinatesTrusted { get; set; }
    /// <summary>2x3 affine matrix mapping frame-pixel homogeneous coords to canvas-pixel coords.</summary>
    public required double[][] PixelToCanvas { get; set; }
    /// <summary>2x3 affine matrix mapping canvas-pixel coords back to frame-pixel coords.</summary>
    public required double[][] CanvasToPixel { get; set; }
}

public sealed class TileFrameScore
{
    public required string FrameId { get; set; }
    public double MeanSharpness { get; set; }
    public int CoveredPixels { get; set; }
    public int ChosenPixels { get; set; }
    public bool Excluded { get; set; }
    public bool ForcedOverride { get; set; }
}

public enum TileState
{
    Pending,
    Processing,
    Validated,
    Failed,
}

public sealed class TileRecord
{
    public required int Index { get; set; }
    public required int X { get; set; }
    public required int Y { get; set; }
    public required int Width { get; set; }
    public required int Height { get; set; }
    public TileState State { get; set; } = TileState.Pending;
    public string? FailureReason { get; set; }
    public string? ManualFrameId { get; set; }
    public List<TileFrameScore> Scores { get; set; } = new();
    public Dictionary<string, int> TrustPixelCounts { get; set; } = new();
}

public enum DraftState
{
    Pending,
    Processing,
    Ready,
    Publishing,
    Published,
    Cancelled,
    Failed,
}

public sealed class CompositeRecord
{
    public required string CompositeId { get; set; }
    public required string JobId { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    // draft / working state
    public DraftState State { get; set; } = DraftState.Pending;
    public long Revision { get; set; } = 1;
    public string? InterruptionReason { get; set; }
    public List<string> FrameIds { get; set; } = new();
    public HashSet<string> ExcludedFrameIds { get; set; } = new();

    // canvas geometry (null until candidate is planned)
    public int? CanvasWidth { get; set; }
    public int? CanvasHeight { get; set; }
    public double? CanvasPixelSizeUm { get; set; }
    public double? OriginXUm { get; set; }
    public double? OriginYUm { get; set; }
    public int TileSize { get; set; } = 64;
    public List<TileRecord> Tiles { get; set; } = new();
    public List<FrameTransformInfo> Transforms { get; set; } = new();
    public List<AlignmentDiagnostic> Diagnostics { get; set; } = new();
    public List<string> ExcludedFromGeometryFrameIds { get; set; } = new();

    public List<string> VersionIds { get; set; } = new();
    public string? PublishedVersionId { get; set; }
    public string? BasedOnVersionId { get; set; }
}

public sealed class VersionFingerprint
{
    public required string FrameId { get; set; }
    public required string OriginalFileName { get; set; }
    public required string Sha256 { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double PixelSizeUm { get; set; }
    public double? Magnification { get; set; }
    public double? ZUm { get; set; }
    public double? StageXUm { get; set; }
    public double? StageYUm { get; set; }
    public DateTimeOffset? CapturedAt { get; set; }
}

public sealed class VersionRecord
{
    public required string VersionId { get; set; }
    public required string CompositeId { get; set; }
    public required string JobId { get; set; }
    public int Sequence { get; set; }
    public required string ParentVersionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
    public string Label { get; set; } = "";

    public required string RuleVersion { get; set; }
    public required Dictionary<string, string> Parameters { get; set; }
    public required List<VersionFingerprint> Fingerprints { get; set; }
    public List<string> ExcludedFrameIds { get; set; } = new();

    public int CanvasWidth { get; set; }
    public int CanvasHeight { get; set; }
    public double CanvasPixelSizeUm { get; set; }
    public double OriginXUm { get; set; }
    public double OriginYUm { get; set; }
    public int TileSize { get; set; }
    public List<TileRecord> Tiles { get; set; } = new();
    public List<FrameTransformInfo> Transforms { get; set; } = new();
    public List<AlignmentDiagnostic> Diagnostics { get; set; } = new();

    public required string CompositeFile { get; set; }
    public required string SourceIndexFile { get; set; }
    public required string TrustFile { get; set; }
    public required string ManifestFile { get; set; }
}

public enum ImportStatus
{
    Verified,
    Failed,
}

public sealed class PixelAudit
{
    public long PixelsChecked { get; set; }
    public long ProvenanceMismatches { get; set; }
    public long TrustMismatches { get; set; }
    public long FileHashMismatches { get; set; }
    public List<string> MissingFrameFingerprints { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

public sealed class ImportRecord
{
    public required string ImportId { get; set; }
    public required string JobId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string PackageName { get; set; }
    public ImportStatus Status { get; set; }
    public required string RuleVersion { get; set; }
    public required PixelAudit Audit { get; set; }
    public string? MatchedVersionId { get; set; }
}

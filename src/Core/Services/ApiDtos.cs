using MicroStack.Core.Models;

namespace MicroStack.Core.Services;

public sealed class IngestResult
{
    public string EvidenceId { get; set; } = "";
    public string ContentSha256 { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Duplicate { get; set; }
}

public sealed class FrameInput
{
    public string EvidenceId { get; set; } = "";
    public double? Magnification { get; set; }
    public double? StageX { get; set; }
    public double? StageY { get; set; }
    public string? StageXUnit { get; set; }
    public string? StageYUnit { get; set; }
    public double ZHeightUm { get; set; }
    public string? CapturedAt { get; set; }
}

public sealed class CreateCompositionRequest
{
    public string? Name { get; set; }
    public List<FrameInput> Frames { get; set; } = [];
    public EngineParameters? Parameters { get; set; }
    public string? Note { get; set; }
}

public sealed class TileOperation
{
    public int TileIndex { get; set; }
    /// <summary>replace | add-exclusion | remove-exclusion</summary>
    public string Kind { get; set; } = "replace";
    public List<string> ExcludedEvidenceIds { get; set; } = [];
    public string? ForcedEvidenceId { get; set; }
}

public sealed class CreateVersionRequest
{
    public string BaseVersionId { get; set; } = "";
    public string? Note { get; set; }
    public List<TileOperation> Operations { get; set; } = [];
}

public sealed class TileValidationRequest
{
    public int TileIndex { get; set; }
    public bool Validated { get; set; }
}

public sealed class PublishRequest
{
    public string VersionId { get; set; } = "";
}

public sealed class PublishResult
{
    public string PublishedVersionId { get; set; } = "";
    public int Revision { get; set; }
}

public sealed class ConflictTile
{
    public int TileIndex { get; set; }
    public string ServerVersionId { get; set; } = "";
    public string ClientBaseVersionId { get; set; } = "";
    public List<string> ServerExcludedEvidenceIds { get; set; } = [];
    public string? ServerForcedEvidenceId { get; set; }
    public List<string> ClientExcludedEvidenceIds { get; set; } = [];
    public string? ClientForcedEvidenceId { get; set; }
}

public sealed class ConflictResult
{
    public string Error { get; set; } = "version-conflict";
    public string Message { get; set; } = "";
    public string CurrentVersionId { get; set; } = "";
    public int Revision { get; set; }
    public List<ConflictTile> Conflicts { get; set; } = [];
}

public sealed class VersionResult
{
    public string VersionId { get; set; } = "";
    public string ParentVersionId { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string Note { get; set; } = "";
    public string RuleSetVersion { get; set; } = "";
    public EngineParameters Parameters { get; set; } = new();
    public double ReferenceMagnification { get; set; }
    public int CanvasWidth { get; set; }
    public int CanvasHeight { get; set; }
    public List<FrameSummary> Frames { get; set; } = [];
    public List<TileSummary> Tiles { get; set; } = [];
    public List<DiagnosticSummary> Diagnostics { get; set; } = [];
    public bool IsPublished { get; set; }
    public bool AutoMerged { get; set; }
}

public sealed class FrameSummary
{
    public string EvidenceId { get; set; } = "";
    public string ContentSha256 { get; set; } = "";
    public string OriginalFilename { get; set; } = "";
    public string ReceivedAt { get; set; } = "";
    public double Magnification { get; set; }
    public bool MagnificationMissing { get; set; }
    public double? StageX { get; set; }
    public double? StageY { get; set; }
    public string? StageXUnit { get; set; }
    public string? StageYUnit { get; set; }
    public double ZHeightUm { get; set; }
    public string? CapturedAt { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class TileSummary
{
    public int Index { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Validated { get; set; }
    public List<string> ExcludedEvidenceIds { get; set; } = [];
    public string? ForcedEvidenceId { get; set; }
    public string? SelectedEvidenceId { get; set; }
    public int TrustedPixels { get; set; }
    public Dictionary<string, int> UntrustedPixels { get; set; } = [];
    public List<FrameScoreDto> Scores { get; set; } = [];
}

public sealed class FrameScoreDto
{
    public string EvidenceId { get; set; } = "";
    public double MeanSharpness { get; set; }
}

public sealed class DiagnosticSummary
{
    public string EvidenceId { get; set; } = "";
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public bool Placed { get; set; }
}

public sealed class CompositionSummary
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public int Revision { get; set; }
    public string? PublishedVersionId { get; set; }
    public string? LatestVersionId { get; set; }
    public int FrameCount { get; set; }
    public int VersionCount { get; set; }
}

public sealed class CompositionDetail
{
    public CompositionSummary Summary { get; set; } = new();
    public List<VersionResult> Versions { get; set; } = [];
    public List<FrameSummary> Frames { get; set; } = [];
}

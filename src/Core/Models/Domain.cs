namespace MicroStack.Core.Models;

/// <summary>Unit declared for a stage coordinate. Null means the upload omitted the unit.</summary>
public sealed record StagePosition(double? X, double? Y, string? XUnit, string? YUnit)
{
    public bool HasMicrons => X.HasValue && Y.HasValue &&
        IsMicron(XUnit) && IsMicron(YUnit);

    public static bool IsMicron(string? unit) =>
        unit is "um" or "µm" or "micron" or "microns";
}

/// <summary>Immutable record of an accepted raw upload. The referenced file is never rewritten.</summary>
public sealed class EvidenceMeta
{
    public string EvidenceId { get; set; } = "";
    public string OriginalFilename { get; set; } = "";
    public string ContentSha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public string ReceivedAt { get; set; } = "";
    public string MediaType { get; set; } = "image/png";
    public int Width { get; set; }
    public int Height { get; set; }
    public int Channels { get; set; }
}

/// <summary>One input frame as bound into a composition (a snapshot of metadata at bind time).</summary>
public sealed class FrameRef
{
    public string EvidenceId { get; set; } = "";
    public string ContentSha256 { get; set; } = "";
    public string OriginalFilename { get; set; } = "";
    public string ReceivedAt { get; set; } = "";
    public double Magnification { get; set; }
    public bool MagnificationMissing { get; set; }
    public StagePosition Stage { get; set; } = new(null, null, null, null);
    public double ZHeightUm { get; set; }
    public string? CapturedAt { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Channels { get; set; }
}

/// <summary>Result of checking whether a frame can participate in alignment.</summary>
public sealed record FrameDiagnostic(
    string EvidenceId,
    string Code,
    string Message,
    bool Placed)
{
    // Codes: UnitsMissing, MagnificationConflict, CalibrationMissing, PlacementOk
}

public sealed class TileRec
{
    public int Index { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Validated { get; set; }
    /// <summary>Evidence ids excluded as dust frames for this tile (manual).</summary>
    public List<string> ExcludedEvidenceIds { get; set; } = [];
    /// <summary>When set, this tile is forced to use a single source frame (manual).</summary>
    public string? ForcedEvidenceId { get; set; }
}

/// <summary>A per-frame selection override applied when creating a candidate.</summary>
public sealed class TileOverride
{
    public int TileIndex { get; set; }
    public List<string> ExcludedEvidenceIds { get; set; } = [];
    public string? ForcedEvidenceId { get; set; }
}

public sealed class VersionRec
{
    public string VersionId { get; set; } = "";
    public string ParentVersionId { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string Note { get; set; } = "";

    public string RuleSetVersion { get; set; } = "";
    public EngineParameters Parameters { get; set; } = new();

    public List<FrameRef> Frames { get; set; } = [];
    public List<TileRec> Tiles { get; set; } = [];
    public List<FrameDiagnostic> FrameDiagnostics { get; set; } = [];

    /// <summary>Artifacts stored content-addressed under artifacts/.</summary>
    public string CompositeArtifact { get; set; } = "";
    public string OverlayArtifact { get; set; } = "";
    public string SourceMaskArtifact { get; set; } = "";
    public string ScoresArtifact { get; set; } = "";
    public string TransformArtifact { get; set; } = "";

    public int CanvasWidth { get; set; }
    public int CanvasHeight { get; set; }
    public double ReferenceMagnification { get; set; }
}

public sealed class CompositionDoc
{
    public string Id { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Bound input frames. Evidence behind them is immutable.</summary>
    public List<FrameRef> Frames { get; set; } = [];

    /// <summary>Append-only chain of candidate/published versions.</summary>
    public List<VersionRec> Versions { get; set; } = [];

    /// <summary>Currently published version id; null means nothing released yet.</summary>
    public string? PublishedVersionId { get; set; }

    public int Revision { get; set; }
}

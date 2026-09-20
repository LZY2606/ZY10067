namespace MicroStack.Core.Exchange;

public sealed class PackageFrame
{
    public int FrameIndex { get; set; }
    public string EvidenceId { get; set; } = "";
    public string ContentSha256 { get; set; } = "";
    public string OriginalFilename { get; set; } = "";
    public string ReceivedAt { get; set; } = "";
    public string EvidenceFile { get; set; } = "";
}

public sealed class ExportManifest
{
    public string PackageFormat { get; set; } = "microstack-export";
    public string PackageFormatVersion { get; set; } = "1.0.0";
    public string ExportedAt { get; set; } = "";
    public string CompositionId { get; set; } = "";
    public string CompositionName { get; set; } = "";
    public string VersionId { get; set; } = "";
    public string ParentVersionId { get; set; } = "";
    public string RuleSetVersion { get; set; } = "";
    public int CanvasWidth { get; set; }
    public int CanvasHeight { get; set; }
    public string CompositeFile { get; set; } = "composite.png";
    public string SourceMaskFile { get; set; } = "source-mask.png";
    public string OverlayFile { get; set; } = "overlay.png";
    public string TransformsFile { get; set; } = "transforms.json";
    public string ScoresFile { get; set; } = "scores.json";
    public List<PackageFrame> Frames { get; set; } = [];
}

public sealed class VerificationIssue
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public long? PixelIndex { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
}

public sealed class ImportReport
{
    public string PackageVersionId { get; set; } = "";
    public string RuleSetVersion { get; set; } = "";
    public string CompositeSha256 { get; set; } = "";
    public string RecomputedSha256 { get; set; } = "";
    public int CanvasWidth { get; set; }
    public int CanvasHeight { get; set; }
    public int PixelsChecked { get; set; }
    public int MismatchedPixels { get; set; }
    public bool Verified { get; set; }
    public List<VerificationIssue> Issues { get; set; } = [];
}

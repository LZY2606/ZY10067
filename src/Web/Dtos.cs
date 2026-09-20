namespace MicroFocus.Web;

public sealed record CreateJobRequest(string? Name);
public sealed record CreateCompositeRequest(string? Name);
public sealed record RevisionRequest(long Revision);
public sealed record ExclusionRequest(long Revision, bool Excluded);
public sealed record TileSourceRequest(long Revision, string? FrameId);
public sealed record PublishRequest(long Revision, string? Label);
public sealed record RepublishRequest(long Revision, string VersionId, string? Label);

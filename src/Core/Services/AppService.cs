using MicroStack.Core.Engine;
using MicroStack.Core.Imaging;
using MicroStack.Core.Models;
using MicroStack.Core.Storage;
using System.Security.Cryptography;
using System.Text.Json;

namespace MicroStack.Core.Services;

public sealed class ServiceException : Exception
{
    public int StatusCode { get; }
    public object? Payload { get; }
    public ServiceException(int statusCode, string message, object? payload = null) : base(message)
    {
        StatusCode = statusCode;
        Payload = payload;
    }
}

/// <summary>
/// Application boundary: evidence ingestion, composition lifecycle, version chain with
/// optimistic tile-level concurrency, gating and atomic publish.
/// </summary>
public sealed partial class AppService
{
    private readonly IObjectStore _objects;
    private readonly JsonDocStore _docs = new();
    private readonly string _compositionsDir;
    private readonly string _evidenceDir;
    private readonly object _lockMapGate = new();
    private readonly Dictionary<string, SemaphoreSlim> _locks = new();

    public AppService(string dataDir, IObjectStore? objects = null)
    {
        DataDir = Path.GetFullPath(dataDir);
        _compositionsDir = Path.Combine(DataDir, "compositions");
        _evidenceDir = Path.Combine(DataDir, "evidence");
        Directory.CreateDirectory(_compositionsDir);
        Directory.CreateDirectory(_evidenceDir);
        _objects = objects ?? new LocalObjectStore(Path.Combine(DataDir, "objects"));
        CleanStagingFiles();
    }

    /// <summary>
    /// Remove tmp staging files left by a process killed mid-write. Completed writes
    /// already renamed into place, so removing staging files cannot touch live state.
    /// </summary>
    private void CleanStagingFiles()
    {
        foreach (var dir in new[] { _compositionsDir, _evidenceDir, DataDir, _objects.RootPath })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var tmp in Directory.EnumerateFiles(dir, "*.tmp-*", SearchOption.AllDirectories))
            {
                try { File.Delete(tmp); } catch { /* best effort */ }
            }
        }
    }

    public string DataDir { get; }

    private SemaphoreSlim LockFor(string compositionId)
    {
        lock (_lockMapGate)
        {
            if (!_locks.TryGetValue(compositionId, out var s))
                _locks[compositionId] = s = new SemaphoreSlim(1, 1);
            return s;
        }
    }

    private string DocPath(string id) => Path.Combine(_compositionsDir, id + ".json");

    private async Task<CompositionDoc> LoadAsync(string id)
    {
        var doc = await _docs.ReadAsync<CompositionDoc>(DocPath(id));
        if (string.IsNullOrEmpty(doc.Id))
            throw new ServiceException(404, $"composition '{id}' not found");
        return doc;
    }

    private async Task SaveAsync(CompositionDoc doc, CancellationToken ct = default)
    {
        doc.Revision++;
        await _docs.WriteAtomicAsync(DocPath(doc.Id), doc, ct);
    }

    // ---------------- Evidence ----------------

    public async Task<IngestResult> IngestEvidenceAsync(
        string filename, byte[] bytes, CancellationToken ct = default)
    {
        PngImage image;
        try { image = Png.Decode(bytes); }
        catch (Exception ex)
        {
            throw new ServiceException(400, $"failed to decode PNG '{filename}': {ex.Message}");
        }

        string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string metaPath = Path.Combine(_evidenceDir, sha + ".json");
        if (File.Exists(metaPath))
        {
            var existing = await _docs.ReadAsync<EvidenceMeta>(metaPath, ct);
            return new IngestResult
            {
                EvidenceId = existing.EvidenceId,
                ContentSha256 = existing.ContentSha256,
                Width = existing.Width,
                Height = existing.Height,
                Duplicate = true
            };
        }

        await _objects.PutAsync(bytes, ".png", ct);
        string evidenceId = "ev_" + sha[..16];
        var meta = new EvidenceMeta
        {
            EvidenceId = evidenceId,
            OriginalFilename = filename,
            ContentSha256 = sha,
            SizeBytes = bytes.Length,
            ReceivedAt = DateTimeOffset.UtcNow.ToString("O"),
            Width = image.Width,
            Height = image.Height,
            Channels = image.Channels
        };
        await _docs.WriteAtomicAsync(metaPath, meta, ct);
        return new IngestResult
        {
            EvidenceId = evidenceId,
            ContentSha256 = sha,
            Width = image.Width,
            Height = image.Height,
            Duplicate = false
        };
    }

    public async Task<(EvidenceMeta Meta, byte[] Bytes)> LoadEvidenceAsync(
        string evidenceId, CancellationToken ct = default)
    {
        string? found = null;
        foreach (var path in Directory.EnumerateFiles(_evidenceDir, "*.json"))
        {
            var meta = await _docs.ReadAsync<EvidenceMeta>(path, ct);
            if (meta.EvidenceId == evidenceId) { found = path; break; }
        }
        if (found == null) throw new ServiceException(404, $"evidence '{evidenceId}' not found");
        var m = await _docs.ReadAsync<EvidenceMeta>(found, ct);
        var bytes = await _objects.ReadAsync(m.ContentSha256 + ".png", ct);
        return (m, bytes);
    }

    public List<EvidenceMeta> ListEvidence()
    {
        var list = new List<EvidenceMeta>();
        if (!Directory.Exists(_evidenceDir)) return list;
        foreach (var path in Directory.EnumerateFiles(_evidenceDir, "*.json"))
            list.Add(_docs.ReadAsync<EvidenceMeta>(path).GetAwaiter().GetResult());
        return list.OrderBy(m => m.ReceivedAt).ToList();
    }

    // ---------------- Composition ----------------

    public async Task<CompositionDoc> CreateCompositionAsync(
        CreateCompositionRequest request, CancellationToken ct = default)
    {
        if (request.Frames.Count == 0)
            throw new ServiceException(400, "a composition needs at least one frame");
        if (request.Frames.Count > 254)
            throw new ServiceException(400,
                "at most 254 input frames per composition (source-index mask is 8-bit; 255 means no source)");

        var frames = new List<FrameRef>();
        foreach (var input in request.Frames)
        {
            var (meta, _) = await LoadEvidenceAsync(input.EvidenceId, ct);
            frames.Add(new FrameRef
            {
                EvidenceId = meta.EvidenceId,
                ContentSha256 = meta.ContentSha256,
                OriginalFilename = meta.OriginalFilename,
                ReceivedAt = meta.ReceivedAt,
                Magnification = input.Magnification ?? 0,
                MagnificationMissing = input.Magnification is null,
                Stage = new StagePosition(input.StageX, input.StageY, input.StageXUnit, input.StageYUnit),
                ZHeightUm = input.ZHeightUm,
                CapturedAt = input.CapturedAt,
                Width = meta.Width,
                Height = meta.Height,
                Channels = meta.Channels
            });
        }

        string id = "cmp_" + Guid.NewGuid().ToString("N")[..12];
        var doc = new CompositionDoc
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(request.Name) ? $"Composition {id[^6..]}" : request.Name.Trim(),
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            Frames = frames,
            Revision = 0
        };

        var parameters = request.Parameters ?? new EngineParameters();
        var firstVersion = await BuildVersionAsync(doc, parent: null, parameters, request.Note ?? "initial candidate",
            overrides: null, ct);
        doc.Versions.Add(firstVersion);
        await SaveAsync(doc, ct);
        return doc;
    }

    private async Task<VersionRec> BuildVersionAsync(
        CompositionDoc doc,
        VersionRec? parent,
        EngineParameters parameters,
        string note,
        IReadOnlyList<TileOverride>? overrides,
        CancellationToken ct)
    {
        var images = new Dictionary<string, PngImage>();
        foreach (var f in doc.Frames)
        {
            var bytes = await _objects.ReadAsync(f.ContentSha256 + ".png", ct);
            images[f.EvidenceId] = Png.Decode(bytes);
        }

        var compositor = new Compositor(parameters);
        var result = compositor.Render(doc.Frames, images, parent?.Tiles, overrides);

        var version = new VersionRec
        {
            VersionId = "ver_" + Guid.NewGuid().ToString("N")[..12],
            ParentVersionId = parent?.VersionId ?? "",
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            Note = note,
            RuleSetVersion = RuleSet.Version,
            Parameters = parameters,
            Frames = CloneFrames(doc.Frames),
            Tiles = result.TileInfos.Select(i => i.Tile).ToList(),
            FrameDiagnostics = result.Layout.Diagnostics,
            CanvasWidth = result.Layout.CanvasWidth,
            CanvasHeight = result.Layout.CanvasHeight,
            ReferenceMagnification = result.Layout.ReferenceMagnification,
            CompositeArtifact = await _objects.PutAsync(result.Artifacts.CompositePng, ".png", ct),
            OverlayArtifact = await _objects.PutAsync(result.Artifacts.OverlayPng, ".png", ct),
            SourceMaskArtifact = await _objects.PutAsync(result.Artifacts.SourceMaskPng, ".png", ct),
            ScoresArtifact = await _objects.PutAsync(
                System.Text.Encoding.UTF8.GetBytes(result.Artifacts.ScoresJson), ".json", ct),
            TransformArtifact = await _objects.PutAsync(
                System.Text.Encoding.UTF8.GetBytes(result.Artifacts.TransformJson), ".json", ct)
        };
        return version;
    }

    private static List<FrameRef> CloneFrames(IReadOnlyList<FrameRef> frames) =>
        frames.Select(f => new FrameRef
        {
            EvidenceId = f.EvidenceId,
            ContentSha256 = f.ContentSha256,
            OriginalFilename = f.OriginalFilename,
            ReceivedAt = f.ReceivedAt,
            Magnification = f.Magnification,
            MagnificationMissing = f.MagnificationMissing,
            Stage = new StagePosition(f.Stage.X, f.Stage.Y, f.Stage.XUnit, f.Stage.YUnit),
            ZHeightUm = f.ZHeightUm,
            CapturedAt = f.CapturedAt,
            Width = f.Width,
            Height = f.Height,
            Channels = f.Channels
        }).ToList();

    public Task<List<CompositionDoc>> ListCompositionsAsync()
    {
        var list = new List<CompositionDoc>();
        if (Directory.Exists(_compositionsDir))
        {
            foreach (var path in Directory.EnumerateFiles(_compositionsDir, "*.json"))
                list.Add(_docs.ReadAsync<CompositionDoc>(path).GetAwaiter().GetResult());
        }
        return Task.FromResult(list.OrderBy(d => d.CreatedAt).ToList());
    }

    public Task<CompositionDoc> GetCompositionAsync(string id) => LoadAsync(id);

    public async Task<byte[]> GetArtifactAsync(string compositionId, string versionId, string kind, CancellationToken ct = default)
    {
        var doc = await LoadAsync(compositionId);
        var v = doc.Versions.FirstOrDefault(x => x.VersionId == versionId)
            ?? throw new ServiceException(404, $"version '{versionId}' not found");
        string key = kind switch
        {
            "composite" => v.CompositeArtifact,
            "overlay" => v.OverlayArtifact,
            "source-mask" => v.SourceMaskArtifact,
            "scores" => v.ScoresArtifact,
            "transforms" => v.TransformArtifact,
            _ => throw new ServiceException(400, $"unknown artifact kind '{kind}'")
        };
        return await _objects.ReadAsync(key, ct);
    }

    // ---------------- Versions: optimistic tile edits ----------------

    public sealed record CreateVersionOutcome(VersionRec Version, bool AutoMerged, List<int> MergedTiles);

    public async Task<CreateVersionOutcome> CreateVersionAsync(
        string compositionId, CreateVersionRequest request, CancellationToken ct = default)
    {
        var sem = LockFor(compositionId);
        await sem.WaitAsync(ct);
        try
        {
            var doc = await LoadAsync(compositionId);
            var baseVersion = doc.Versions.FirstOrDefault(v => v.VersionId == request.BaseVersionId)
                ?? throw new ServiceException(404, $"base version '{request.BaseVersionId}' not found");
            var latest = doc.Versions[^1];

            var clientOps = NormalizeOperations(request.Operations);
            var clientTiles = clientOps.Keys.ToHashSet();

            if (baseVersion.VersionId == latest.VersionId)
            {
                var v = await BuildVersionAsync(doc, baseVersion, baseVersion.Parameters,
                    request.Note ?? "candidate", ToOverrides(baseVersion, clientOps, doc), ct);
                doc.Versions.Add(v);
                await SaveAsync(doc, ct);
                return new CreateVersionOutcome(v, false, clientTiles.ToList());
            }

            // Client is stale. Determine tile edits that happened on the server path
            // base -> ... -> latest, and auto-merge only disjoint tile sets.
            var serverEdited = TilesChangedBetween(baseVersion, latest);
            var clash = clientTiles.Intersect(serverEdited).OrderBy(x => x).ToList();
            if (clash.Count > 0)
            {
                var conflicts = clash.Select(t =>
                {
                    var b = TileOf(baseVersion, t);
                    var s = TileOf(latest, t);
                    var c = clientOps[t];
                    return new ConflictTile
                    {
                        TileIndex = t,
                        ServerVersionId = latest.VersionId,
                        ClientBaseVersionId = baseVersion.VersionId,
                        ServerExcludedEvidenceIds = s.ExcludedEvidenceIds,
                        ServerForcedEvidenceId = s.ForcedEvidenceId,
                        ClientExcludedEvidenceIds = ResolveOp(b, c).ExcludedEvidenceIds,
                        ClientForcedEvidenceId = ResolveOp(b, c).ForcedEvidenceId
                    };
                }).ToList();
                var payload = new ConflictResult
                {
                    Message = $"base version '{request.BaseVersionId}' is stale; {clash.Count} tile(s) changed on both branches",
                    CurrentVersionId = latest.VersionId,
                    Revision = doc.Revision,
                    Conflicts = conflicts
                };
                throw new ServiceException(409, "version conflict", payload);
            }

            // Disjoint edits: rebase client operations onto the latest version.
            var v2 = await BuildVersionAsync(doc, latest, latest.Parameters,
                (request.Note ?? "candidate") + " (auto-merged)", ToOverrides(latest, clientOps, doc), ct);
            doc.Versions.Add(v2);
            await SaveAsync(doc, ct);
            return new CreateVersionOutcome(v2, true, clientTiles.ToList());
        }
        finally { sem.Release(); }
    }

    private Dictionary<int, (List<string> Excluded, string? Forced)> NormalizeOperations(List<TileOperation> ops)
    {
        var byTile = new Dictionary<int, (List<string>, string?)>();
        foreach (var op in ops)
        {
            byTile.TryGetValue(op.TileIndex, out var cur);
            var list = cur.Item1 ?? new List<string>();
            string? forced = cur.Item2;
            switch (op.Kind)
            {
                case "replace":
                    list = op.ExcludedEvidenceIds.ToList();
                    forced = op.ForcedEvidenceId;
                    break;
                case "add-exclusion":
                    foreach (var e in op.ExcludedEvidenceIds) if (!list.Contains(e)) list.Add(e);
                    if (op.ForcedEvidenceId != null) forced = op.ForcedEvidenceId;
                    break;
                case "remove-exclusion":
                    list = list.Where(x => !op.ExcludedEvidenceIds.Contains(x)).ToList();
                    if (op.ForcedEvidenceId == "") forced = null;
                    break;
                default:
                    throw new ServiceException(400, $"unknown tile operation '{op.Kind}'");
            }
            byTile[op.TileIndex] = (list, forced);
        }
        return byTile;
    }

    private static TileRec TileOf(VersionRec v, int index) =>
        v.Tiles.FirstOrDefault(t => t.Index == index)
        ?? throw new ServiceException(400, $"tile {index} does not exist in version {v.VersionId}");

    private static (List<string> ExcludedEvidenceIds, string? ForcedEvidenceId) ResolveOp(
        TileRec baseTile, (List<string> Excluded, string? Forced) op) =>
        (op.Excluded.OrderBy(x => x).ToList(), op.Forced);

    private List<TileOverride> ToOverrides(
        VersionRec baseVersion,
        Dictionary<int, (List<string> Excluded, string? Forced)> ops,
        CompositionDoc doc)
    {
        var known = doc.Frames.Select(f => f.EvidenceId).ToHashSet();
        var result = new List<TileOverride>();
        foreach (var (idx, (excluded, forced)) in ops)
        {
            var tile = TileOf(baseVersion, idx);
            foreach (var e in excluded)
                if (!known.Contains(e)) throw new ServiceException(400, $"unknown evidence id '{e}'");
            if (forced != null && !known.Contains(forced))
                throw new ServiceException(400, $"unknown evidence id '{forced}'");
            result.Add(new TileOverride
            {
                TileIndex = tile.Index,
                ExcludedEvidenceIds = excluded,
                ForcedEvidenceId = forced
            });
        }
        return result;
    }

    private static HashSet<int> TilesChangedBetween(VersionRec a, VersionRec b)
    {
        var changed = new HashSet<int>();
        var tb = b.Tiles.ToDictionary(t => t.Index);
        foreach (var ta in a.Tiles)
        {
            if (!tb.TryGetValue(ta.Index, out var other)) { changed.Add(ta.Index); continue; }
            if (ta.ForcedEvidenceId != other.ForcedEvidenceId ||
                !ta.ExcludedEvidenceIds.OrderBy(x => x).SequenceEqual(other.ExcludedEvidenceIds.OrderBy(x => x)))
            {
                changed.Add(ta.Index);
            }
        }
        return changed;
    }

    // ---------------- Tile validation ----------------

    public async Task<VersionRec> SetTileValidationAsync(
        string compositionId, string versionId, TileValidationRequest request, CancellationToken ct = default)
    {
        var sem = LockFor(compositionId);
        await sem.WaitAsync(ct);
        try
        {
            var doc = await LoadAsync(compositionId);
            var v = doc.Versions.FirstOrDefault(x => x.VersionId == versionId)
                ?? throw new ServiceException(404, $"version '{versionId}' not found");
            var tile = v.Tiles.FirstOrDefault(t => t.Index == request.TileIndex)
                ?? throw new ServiceException(400, $"tile {request.TileIndex} does not exist");
            tile.Validated = request.Validated;
            await SaveAsync(doc, ct);
            return v;
        }
        finally { sem.Release(); }
    }

    /// <summary>Discard an unpublished candidate. Never touches the published pointer.</summary>
    public async Task CancelVersionAsync(string compositionId, string versionId, CancellationToken ct = default)
    {
        var sem = LockFor(compositionId);
        await sem.WaitAsync(ct);
        try
        {
            var doc = await LoadAsync(compositionId);
            var v = doc.Versions.FirstOrDefault(x => x.VersionId == versionId)
                ?? throw new ServiceException(404, $"version '{versionId}' not found");
            if (v.VersionId == doc.PublishedVersionId)
                throw new ServiceException(400, "the published version cannot be canceled");
            doc.Versions.Remove(v);
            await SaveAsync(doc, ct);
        }
        finally { sem.Release(); }
    }

    // ---------------- Publish ----------------

    public async Task<PublishResult> PublishAsync(string compositionId, PublishRequest request, CancellationToken ct = default)
    {
        var sem = LockFor(compositionId);
        await sem.WaitAsync(ct);
        try
        {
            var doc = await LoadAsync(compositionId);
            var v = doc.Versions.FirstOrDefault(x => x.VersionId == request.VersionId)
                ?? throw new ServiceException(404, $"version '{request.VersionId}' not found");
            if (v.Tiles.Count == 0)
                throw new ServiceException(400, "version has no tiles");
            var unvalidated = v.Tiles.Where(t => !t.Validated).Select(t => t.Index).ToList();
            if (unvalidated.Count > 0)
                throw new ServiceException(409,
                    $"all tiles must be validated before publish; {unvalidated.Count} remain",
                    new { error = "tiles-unvalidated", unvalidatedTiles = unvalidated });

            string? previous = doc.PublishedVersionId;
            doc.PublishedVersionId = v.VersionId;
            await SaveAsync(doc, ct);
            return new PublishResult { PublishedVersionId = v.VersionId, Revision = doc.Revision };
        }
        finally { sem.Release(); }
    }

    public CompositionDetail ToDetail(CompositionDoc doc)
    {
        return new CompositionDetail
        {
            Summary = new CompositionSummary
            {
                Id = doc.Id,
                Name = doc.Name,
                CreatedAt = doc.CreatedAt,
                Revision = doc.Revision,
                PublishedVersionId = doc.PublishedVersionId,
                LatestVersionId = doc.Versions.Count > 0 ? doc.Versions[^1].VersionId : null,
                FrameCount = doc.Frames.Count,
                VersionCount = doc.Versions.Count
            },
            Frames = doc.Frames.Select(ToFrameSummary).ToList(),
            Versions = doc.Versions.Select(v => ToVersionResult(doc, v)).ToList()
        };
    }

    public static FrameSummary ToFrameSummary(FrameRef f) => new()
    {
        EvidenceId = f.EvidenceId,
        ContentSha256 = f.ContentSha256,
        OriginalFilename = f.OriginalFilename,
        ReceivedAt = f.ReceivedAt,
        Magnification = f.Magnification,
        MagnificationMissing = f.MagnificationMissing,
        StageX = f.Stage.X,
        StageY = f.Stage.Y,
        StageXUnit = f.Stage.XUnit,
        StageYUnit = f.Stage.YUnit,
        ZHeightUm = f.ZHeightUm,
        CapturedAt = f.CapturedAt,
        Width = f.Width,
        Height = f.Height
    };

    public VersionResult ToVersionResult(CompositionDoc doc, VersionRec v)
    {
        ScoresDoc? scores = null;
        try
        {
            var bytes = _objects.ReadAsync(v.ScoresArtifact).GetAwaiter().GetResult();
            scores = JsonSerializer.Deserialize<ScoresDoc>(bytes, Engine.JsonOptions.Web);
        }
        catch { /* artifact may be unavailable on a damaged store; diagnostics still render */ }

        var tiles = v.Tiles.Select(t =>
        {
            var ts = scores?.Tiles.FirstOrDefault(x => x.TileIndex == t.Index);
            return new TileSummary
            {
                Index = t.Index,
                X = t.X,
                Y = t.Y,
                Width = t.Width,
                Height = t.Height,
                Validated = t.Validated,
                ExcludedEvidenceIds = t.ExcludedEvidenceIds,
                ForcedEvidenceId = t.ForcedEvidenceId,
                SelectedEvidenceId = ts?.SelectedEvidenceId,
                TrustedPixels = ts?.TrustedPixels ?? 0,
                UntrustedPixels = ts?.UntrustedPixels ?? [],
                Scores = ts?.FrameScores.Select(fs => new FrameScoreDto
                {
                    EvidenceId = fs.EvidenceId,
                    MeanSharpness = fs.MeanSharpness
                }).ToList() ?? []
            };
        }).ToList();

        return new VersionResult
        {
            VersionId = v.VersionId,
            ParentVersionId = v.ParentVersionId,
            CreatedAt = v.CreatedAt,
            Note = v.Note,
            RuleSetVersion = v.RuleSetVersion,
            Parameters = v.Parameters,
            ReferenceMagnification = v.ReferenceMagnification,
            CanvasWidth = v.CanvasWidth,
            CanvasHeight = v.CanvasHeight,
            Frames = v.Frames.Select(ToFrameSummary).ToList(),
            Tiles = tiles,
            Diagnostics = v.FrameDiagnostics.Select(d => new DiagnosticSummary
            {
                EvidenceId = d.EvidenceId,
                Code = d.Code,
                Message = d.Message,
                Placed = d.Placed
            }).ToList(),
            IsPublished = doc.PublishedVersionId == v.VersionId
        };
    }
}

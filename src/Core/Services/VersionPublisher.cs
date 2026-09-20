using System.Text.Json;
using System.Text.Json.Serialization;
using MicroFocus.Core.Imaging;
using MicroFocus.Core.Storage;

namespace MicroFocus.Core.Services;

public sealed partial class CompositeService
{
    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public sealed class PublishedView
    {
        public required CompositeRecord Composite { get; init; }
        public required VersionRecord Version { get; init; }
    }

    /// <summary>Freeze the validated candidate into an immutable published version. Every tile must be validated.</summary>
    public PublishedView Publish(string compositeId, long revision, string label)
    {
        CompositeRecord rec;
        lock (LockFor(compositeId))
        {
            rec = _composites.Get<CompositeRecord>(compositeId);
            if (rec.Revision != revision)
                throw new ConflictException($"版本冲突：当前 rev={rec.Revision}，提交基于 rev={revision}")
                { CurrentRevision = rec.Revision };
            if (rec.Tiles.Count == 0 || rec.Tiles.Any(t => t.State != TileState.Validated))
                throw new DomainException("TILES_NOT_VALIDATED",
                    "只有所有瓦片验证通过才能发布；失败瓦片必须改选来源或排除问题帧后重处理。");
            if (rec.CanvasWidth is null || rec.CanvasHeight is null)
                throw new DomainException("NO_CANDIDATE", "候选画布尚未生成。");
            rec.State = DraftState.Publishing;
            rec.Revision++;
            _composites.Put(compositeId, rec);
        }

        try
        {
            int cw = rec.CanvasWidth!.Value, ch = rec.CanvasHeight!.Value;
            var frames = rec.FrameIds.Select(id =>
            {
                var fr = _jobs.GetFrame(id);
                return new Frame { Record = fr, Image = _jobs.LoadImage(fr) };
            }).ToList();
            var plan = AlignmentPlanner.Plan(frames, rec.TileSize);
            var compositor = new Compositor(new CompositorOptions { TileSize = rec.TileSize });
            var overrides = rec.Tiles.Where(t => t.ManualFrameId != null)
                .ToDictionary(t => t.Index, t => t.ManualFrameId!);

            var artifacts = new List<TileArtifact>();
            foreach (var r in compositor.Compose(plan, rec.ExcludedFrameIds, overrides))
            {
                artifacts.Add(new TileArtifact
                {
                    Index = r.Index, X = r.X, Y = r.Y, Width = r.Width, Height = r.Height,
                    Gray = r.Gray, SourceIndex = r.SourceIndex, Trust = r.Trust,
                });
            }
            var canvas = VersionDerivatives.Assemble(artifacts, cw, ch);

            int seq = rec.VersionIds.Count == 0 ? 1 :
                rec.VersionIds.Select(v => _versions.Get<VersionRecord>(v).Sequence).Max() + 1;
            string versionId = "ver_" + seq.ToString("D3") + "_" + Guid.NewGuid().ToString("N")[..8];
            string parent = rec.PublishedVersionId ?? "";

            var parameters = new Dictionary<string, string>
            {
                ["tileSize"] = rec.TileSize.ToString(),
                ["sharpnessMetric"] = "windowed-laplacian-energy",
                ["sharpnessWindow"] = "7x7",
                ["pixelSampling"] = "nearest-neighbor, frame-center mapped",
                ["canvasPixelSizeUm"] = rec.CanvasPixelSizeUm!.Value.ToString("R"),
                ["canvasOriginXUm"] = rec.OriginXUm!.Value.ToString("R"),
                ["canvasOriginYUm"] = rec.OriginYUm!.Value.ToString("R"),
                ["scoreRuleVersion"] = FocusRuleVersion,
                ["alignmentRuleVersion"] = AlignmentPlanner.RuleVersion,
            };

            var version = new VersionRecord
            {
                VersionId = versionId,
                CompositeId = compositeId,
                JobId = rec.JobId,
                Sequence = seq,
                ParentVersionId = parent,
                CreatedAt = DateTimeOffset.UtcNow,
                PublishedAt = DateTimeOffset.UtcNow,
                Label = string.IsNullOrWhiteSpace(label) ? $"v{seq}" : label.Trim(),
                RuleVersion = FocusRuleVersion,
                Parameters = parameters,
                Fingerprints = rec.FrameIds.Select(BuildFingerprint).ToList(),
                ExcludedFrameIds = rec.ExcludedFrameIds.ToList(),
                CanvasWidth = cw,
                CanvasHeight = ch,
                CanvasPixelSizeUm = rec.CanvasPixelSizeUm!.Value,
                OriginXUm = rec.OriginXUm!.Value,
                OriginYUm = rec.OriginYUm!.Value,
                TileSize = rec.TileSize,
                Tiles = rec.Tiles.Select(CloneTile).ToList(),
                Transforms = BuildTransforms(plan),
                Diagnostics = rec.Diagnostics.ToList(),
                CompositeFile = "",
                SourceIndexFile = "",
                TrustFile = "",
                ManifestFile = "",
            };

            var pgm = PnmCodec.EncodeP5(canvas.Gray, cw, ch);
            WriteDerivative(compositeId, versionId, "composite.pgm", pgm);
            WriteDerivative(compositeId, versionId, "source-index.u16", RawEncode(canvas.SourceIndex));
            WriteDerivative(compositeId, versionId, "trust.u8", canvas.Trust);
            version.CompositeFile = Path.Combine(compositeId, versionId, "composite.pgm");
            version.SourceIndexFile = Path.Combine(compositeId, versionId, "source-index.u16");
            version.TrustFile = Path.Combine(compositeId, versionId, "trust.u8");

            // per-tile artifacts kept for provenance drill-down
            foreach (var a in artifacts)
            {
                var bytes = TileCodec.Encode(a.X, a.Y, a.Width, a.Height, a.Gray, a.SourceIndex, a.Trust);
                _derivatives.WriteTile(compositeId, versionId, $"tile_{a.Index:D5}.tile", bytes);
            }

            string manifestJson = JsonSerializer.Serialize(version, ManifestJson);
            WriteDerivative(compositeId, versionId, "manifest.json", System.Text.Encoding.UTF8.GetBytes(manifestJson));
            version.ManifestFile = Path.Combine(compositeId, versionId, "manifest.json");

            _versions.Put(versionId, version);

            lock (LockFor(compositeId))
            {
                var now = _composites.Get<CompositeRecord>(compositeId);
                now.State = DraftState.Published;
                now.PublishedVersionId = versionId;
                now.VersionIds.Add(versionId);
                now.InterruptionReason = null;
                now.Revision++;
                _composites.Put(compositeId, now);
                return new PublishedView { Composite = now, Version = version };
            }
        }
        catch
        {
            MarkFailed(compositeId, "发布失败：新版本未写入，上一已发布版本保持不变。");
            throw;
        }
    }

    /// <summary>Republish an older version's exact inputs/overrides as a new version (append-only). Old artifacts stay intact.</summary>
    public PublishedView RepublishFromVersion(string compositeId, long revision, string sourceVersionId, string label)
    {
        var source = _versions.Get<VersionRecord>(sourceVersionId);
        if (source.CompositeId != compositeId)
            throw new DomainException("VERSION_MISMATCH", "版本不属于该合成。");

        var rec = Mutate(compositeId, revision, r =>
        {
            r.ExcludedFrameIds = new HashSet<string>(source.ExcludedFrameIds);
            r.Tiles = source.Tiles.Select(CloneTile).ToList();
            foreach (var t in r.Tiles)
            {
                t.State = TileState.Validated;
                t.FailureReason = null;
            }
            r.CanvasWidth = source.CanvasWidth;
            r.CanvasHeight = source.CanvasHeight;
            r.CanvasPixelSizeUm = source.CanvasPixelSizeUm;
            r.OriginXUm = source.OriginXUm;
            r.OriginYUm = source.OriginYUm;
            r.TileSize = source.TileSize;
            r.Transforms = source.Transforms.ToList();
            r.Diagnostics = source.Diagnostics.ToList();
            r.BasedOnVersionId = source.VersionId;
            r.State = DraftState.Ready;
        });
        return Publish(compositeId, rec.Revision, label);
    }

    /// <summary>Start a new editable draft initialized from an old version's fingerprints and tile choices.</summary>
    public CompositeRecord DraftFromVersion(string compositeId, long revision, string sourceVersionId)
    {
        var source = _versions.Get<VersionRecord>(sourceVersionId);
        if (source.CompositeId != compositeId)
            throw new DomainException("VERSION_MISMATCH", "版本不属于该合成。");
        return Mutate(compositeId, revision, r =>
        {
            r.ExcludedFrameIds = new HashSet<string>(source.ExcludedFrameIds);
            r.Tiles = source.Tiles.Select(t =>
            {
                var nt = CloneTile(t);
                nt.State = TileState.Validated;
                nt.FailureReason = null;
                return nt;
            }).ToList();
            r.CanvasWidth = source.CanvasWidth;
            r.CanvasHeight = source.CanvasHeight;
            r.CanvasPixelSizeUm = source.CanvasPixelSizeUm;
            r.OriginXUm = source.OriginXUm;
            r.OriginYUm = source.OriginYUm;
            r.TileSize = source.TileSize;
            r.Transforms = source.Transforms.ToList();
            r.Diagnostics = source.Diagnostics.ToList();
            r.BasedOnVersionId = source.VersionId;
            r.State = DraftState.Ready;
            r.PublishedVersionId = r.VersionIds.Count > 0 ? r.PublishedVersionId : null;
            r.InterruptionReason = $"已从 {source.VersionId} 创建可编辑草稿（旧版本保持不可变）。";
        });
    }

    private VersionFingerprint BuildFingerprint(string frameId)
    {
        var f = _jobs.GetFrame(frameId);
        double pxUm = f.PixelSizeUm ?? AlignmentPlanner.NominalPixelSizeUm
            .OrderBy(kv => Math.Abs(kv.Key - (f.Magnification ?? 0))).First().Value;
        return new VersionFingerprint
        {
            FrameId = f.FrameId,
            OriginalFileName = f.OriginalFileName,
            Sha256 = f.Sha256,
            Width = f.Width,
            Height = f.Height,
            PixelSizeUm = pxUm,
            Magnification = f.Magnification,
            ZUm = f.ZUm,
            StageXUm = f.StageXUm,
            StageYUm = f.StageYUm,
            CapturedAt = f.CapturedAt,
        };
    }

    internal static TileRecord CloneTile(TileRecord t) => new()
    {
        Index = t.Index,
        X = t.X,
        Y = t.Y,
        Width = t.Width,
        Height = t.Height,
        State = t.State,
        FailureReason = t.FailureReason,
        ManualFrameId = t.ManualFrameId,
        Scores = t.Scores.Select(s => new TileFrameScore
        {
            FrameId = s.FrameId,
            MeanSharpness = s.MeanSharpness,
            CoveredPixels = s.CoveredPixels,
            ChosenPixels = s.ChosenPixels,
            Excluded = s.Excluded,
            ForcedOverride = s.ForcedOverride,
        }).ToList(),
        TrustPixelCounts = new Dictionary<string, int>(t.TrustPixelCounts),
    };

    private string WriteDerivative(string compositeId, string versionId, string name, byte[] bytes)
    {
        string path = _derivatives.WriteVersionFile(compositeId, versionId, name, bytes);
        return path;
    }

    internal static byte[] RawEncode(ushort[] values)
    {
        var bytes = new byte[values.Length * 2L];
        for (long i = 0; i < values.Length; i++)
        {
            bytes[i * 2] = (byte)(values[i] >> 8);
            bytes[i * 2 + 1] = (byte)(values[i] & 0xFF);
        }
        return bytes;
    }
}

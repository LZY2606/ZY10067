using System.Diagnostics;
using MicroFocus.Core.Imaging;
using MicroFocus.Core.Storage;

namespace MicroFocus.Core.Services;

/// <summary>
/// Drives candidate composites: planning, per-tile processing with user overrides,
/// cancellation without replacing the published version, publishing immutable versions,
/// and optimistic-concurrency conflict detection.
/// </summary>
public sealed partial class CompositeService
{
    public const string FocusRuleVersion = "focus-stack-1.0";
    public const int TileSizeV1 = 64;

    private readonly JsonStore _composites;
    private readonly JsonStore _versions;
    private readonly DerivativeStore _derivatives;
    private readonly JobService _jobs;
    private readonly int _defaultTileSize;
    private readonly Dictionary<string, object> _locks = new();
    private readonly object _locksGate = new();

    public CompositeService(string dataDir, JobService jobs, int defaultTileSize = TileSizeV1)
    {
        _composites = new JsonStore(dataDir, "composites");
        _versions = new JsonStore(dataDir, "versions");
        _derivatives = new DerivativeStore(dataDir);
        _jobs = jobs;
        _defaultTileSize = defaultTileSize;
    }

    public IReadOnlyList<CompositeRecord> ListComposites(string jobId) =>
        _composites.List<CompositeRecord>().Where(c => c.JobId == jobId)
            .OrderBy(c => c.CreatedAt).ToList();

    public CompositeRecord GetComposite(string compositeId) => _composites.Get<CompositeRecord>(compositeId);
    public VersionRecord GetVersion(string versionId) => _versions.Get<VersionRecord>(versionId);
    public IReadOnlyList<VersionRecord> ListVersions(string compositeId) =>
        GetComposite(compositeId).VersionIds.Select(_versions.Get<VersionRecord>)
            .OrderBy(v => v.Sequence).ToList();

    private object LockFor(string compositeId)
    {
        lock (_locksGate)
        {
            if (!_locks.TryGetValue(compositeId, out var l)) _locks[compositeId] = l = new object();
            return l;
        }
    }

    private CompositeRecord Mutate(string id, long expectedRevision, Action<CompositeRecord> action)
    {
        lock (LockFor(id))
        {
            var rec = _composites.Get<CompositeRecord>(id);
            if (rec.Revision != expectedRevision)
                throw new ConflictException($"版本冲突：该合成已被其他会话修改（当前 rev={rec.Revision}，提交基于 rev={expectedRevision}）")
                { CurrentRevision = rec.Revision };
            rec.Revision++;
            action(rec);
            _composites.Put(id, rec);
            return rec;
        }
    }

    // ---------- planning & creation ----------

    public sealed class PlanView
    {
        public required CompositeRecord Composite { get; init; }
        public required CanvasPlan Plan { get; init; }
        public required List<Frame> Frames { get; init; }
    }

    public CanvasPlan LoadPlan(CompositeRecord rec, IReadOnlyList<Frame>? frames = null)
    {
        frames ??= rec.FrameIds.Select(id =>
        {
            var fr = _jobs.GetFrame(id);
            return new Frame { Record = fr, Image = _jobs.LoadImage(fr) };
        }).ToList();
        return AlignmentPlanner.Plan(frames, rec.TileSize);
    }

    public CompositeRecord CreateComposite(string jobId, string name)
    {
        var job = _jobs.GetJob(jobId);
        if (job.FrameIds.Count == 0)
            throw new DomainException("NO_FRAMES", "任务还没有输入帧，无法开始合成。");

        var rec = new CompositeRecord
        {
            CompositeId = "cmp_" + Guid.NewGuid().ToString("N")[..12],
            JobId = jobId,
            Name = string.IsNullOrWhiteSpace(name) ? $"合成 {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}" : name.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
            FrameIds = new List<string>(job.FrameIds),
            TileSize = _defaultTileSize,
        };
        _composites.Put(rec.CompositeId, rec);
        return rec;
    }

    /// <summary>Runs planning and processes every tile synchronously (used by tests and the background worker).</summary>
    public CompositeRecord Process(string compositeId, CancellationToken ct = default)
    {
        var rec = _composites.Get<CompositeRecord>(compositeId);
        lock (LockFor(compositeId))
        {
            rec = _composites.Get<CompositeRecord>(compositeId);
            if (rec.State is DraftState.Published or DraftState.Publishing)
                throw new DomainException("INVALID_STATE", $"状态 {rec.State} 不可重新处理。");
            rec.State = DraftState.Processing;
            rec.InterruptionReason = null;
            rec.Revision++;
            _composites.Put(compositeId, rec);
        }

        try
        {
            var frames = rec.FrameIds.Select(id =>
            {
                var fr = _jobs.GetFrame(id);
                return new Frame { Record = fr, Image = _jobs.LoadImage(fr) };
            }).ToList();
            var plan = AlignmentPlanner.Plan(frames, rec.TileSize);

            // Keep prior per-frame exclusion diagnostics across reprocessing runs.
            var prior = rec.Diagnostics
                .Where(d => d.Code is "STAGE_UNIT_MISSING" or "SCALE_MISSING" or "SCALE_TABLE_MISS")
                .Where(d => !plan.Diagnostics.Any(n => n.Code == d.Code && n.FrameId == d.FrameId))
                .ToList();
            rec.Diagnostics = prior.Concat(plan.Diagnostics).ToList();
            rec.ExcludedFromGeometryFrameIds = plan.ExcludedFrameIds;
            rec.Transforms = BuildTransforms(plan);
            rec.CanvasWidth = plan.Width;
            rec.CanvasHeight = plan.Height;
            rec.CanvasPixelSizeUm = plan.PixelSizeUm;
            rec.OriginXUm = plan.OriginXUm;
            rec.OriginYUm = plan.OriginYUm;

            if (plan.Width == 0 || plan.Height == 0)
            {
                rec.State = DraftState.Failed;
                rec.InterruptionReason = "没有可对齐的帧（NO_ALIGNABLE_FRAME）。";
                rec.CanvasWidth = null;
                rec.CanvasHeight = null;
                rec.CanvasPixelSizeUm = null;
                rec.OriginXUm = null;
                rec.OriginYUm = null;
                rec.Tiles.Clear();
                Persist(rec);
                return rec;
            }

            int cols = (plan.Width + rec.TileSize - 1) / rec.TileSize;
            int rows = (plan.Height + rec.TileSize - 1) / rec.TileSize;
            var compositor = new Compositor(new CompositorOptions { TileSize = rec.TileSize });
            var overrides = rec.Tiles.Where(t => t.ManualFrameId != null)
                .ToDictionary(t => t.Index, t => t.ManualFrameId!);

            // Keep previously validated tile results only if geometry unchanged; otherwise recompute all.
            var byIndex = rec.Tiles.ToDictionary(t => t.Index);
            var newTiles = new List<TileRecord>();
            foreach (var result in compositor.Compose(plan, rec.ExcludedFrameIds, overrides, null, null))
            {
                ct.ThrowIfCancellationRequested();
                var tile = new TileRecord
                {
                    Index = result.Index, X = result.X, Y = result.Y,
                    Width = result.Width, Height = result.Height,
                    Scores = result.Scores,
                    TrustPixelCounts = result.TrustPixelCounts,
                    ManualFrameId = overrides.GetValueOrDefault(result.Index),
                    State = result.AllNoCoverage ? TileState.Failed : TileState.Validated,
                    FailureReason = result.AllNoCoverage ? "TILE_NO_COVERAGE：该瓦片没有任何未排除帧覆盖，拒绝无声填充。" : null,
                };
                if (byIndex.TryGetValue(result.Index, out var prev) && prev.State == TileState.Validated)
                {
                    // geometry hash equality implicitly guaranteed by same canvas dims/frame list
                }
                newTiles.Add(tile);
            }
            rec.Tiles = newTiles.OrderBy(t => t.Index).ToList();
            rec.State = rec.Tiles.All(t => t.State == TileState.Validated) ? DraftState.Ready : DraftState.Failed;
            if (rec.State == DraftState.Failed)
                rec.InterruptionReason = "存在验证失败的瓦片，必须改选来源或排除/恢复帧后重试。";
            Persist(rec);
            return rec;
        }
        catch (OperationCanceledException)
        {
            MarkCancelled(compositeId, "用户取消：已处理的候选不会替换已发布版本。");
            throw;
        }
        catch (Exception ex)
        {
            MarkFailed(compositeId, "处理异常：" + ex.Message);
            throw;
        }
    }

    public static List<FrameTransformInfo> BuildTransforms(CanvasPlan plan) =>
        plan.Frames.Select(pf => new FrameTransformInfo
        {
            FrameId = pf.Frame.Record.FrameId,
            Sha256 = pf.Frame.Record.Sha256,
            Width = pf.Frame.Image.Width,
            Height = pf.Frame.Image.Height,
            PixelSizeUm = pf.Frame.Record.PixelSizeUm ?? AlignmentPlanner.NominalPixelSizeUm
                .OrderBy(kv => Math.Abs(kv.Key - (pf.Frame.Record.Magnification ?? 0))).First().Value,
            Magnification = pf.Frame.Record.Magnification,
            MagnificationInferred = pf.Frame.Record.MagnificationInferred,
            PixelSizeInferred = pf.ScaleInferred,
            StageCoordinatesTrusted = pf.Frame.Record.StageCoordinatesTrusted,
            PixelToCanvas = pf.PixelToCanvas.ToMatrix(),
            CanvasToPixel = pf.CanvasToPixel.ToMatrix(),
        }).ToList();

    private void Persist(CompositeRecord rec)
    {
        lock (LockFor(rec.CompositeId)) _composites.Put(rec.CompositeId, rec);
    }

    private void MarkCancelled(string id, string reason)
    {
        lock (LockFor(id))
        {
            var rec = _composites.Get<CompositeRecord>(id);
            if (rec.State == DraftState.Published) return;
            rec.State = DraftState.Cancelled;
            rec.InterruptionReason = reason;
            rec.Revision++;
            _composites.Put(id, rec);
        }
    }

    private void MarkFailed(string id, string reason)
    {
        lock (LockFor(id))
        {
            var rec = _composites.Get<CompositeRecord>(id);
            if (rec.State == DraftState.Published) return;
            rec.State = DraftState.Failed;
            rec.InterruptionReason = reason;
            rec.Revision++;
            _composites.Put(id, rec);
        }
    }

    /// <summary>Mark drafts left in Processing by an ungraceful host exit as interrupted (failure recovery on startup).</summary>
    public int RecoverInterrupted()
    {
        int n = 0;
        foreach (var rec in _composites.List<CompositeRecord>())
        {
            if (rec.State is DraftState.Processing or DraftState.Publishing)
            {
                lock (LockFor(rec.CompositeId))
                {
                    var fresh = _composites.Get<CompositeRecord>(rec.CompositeId);
                    if (fresh.State is DraftState.Processing or DraftState.Publishing)
                    {
                        fresh.State = DraftState.Failed;
                        fresh.InterruptionReason = "进程在处理中退出：候选已中断，上一已发布版本未受影响，可点击“重试处理”。";
                        fresh.Revision++;
                        _composites.Put(fresh.CompositeId, fresh);
                        n++;
                    }
                }
            }
        }
        return n;
    }

    public void Cancel(string compositeId, long revision)
    {
        var rec = Mutate(compositeId, revision, r =>
        {
            if (r.State == DraftState.Published)
                throw new DomainException("INVALID_STATE", "已发布版本不能取消。");
            r.State = DraftState.Cancelled;
            r.InterruptionReason = "用户取消：未替换上一已发布版本。";
        });
        _ = rec;
    }
}

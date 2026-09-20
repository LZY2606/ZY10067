using MicroFocus.Core.Storage;

namespace MicroFocus.Core.Services;

public sealed partial class CompositeService
{
    public sealed class EditResult
    {
        public required CompositeRecord Composite { get; init; }
        public bool NeedsReprocessing { get; init; }
        public List<int> DirtyTiles { get; init; } = new();
    }

    private CompositeRecord BeginDraftIfPublished(CompositeRecord rec)
    {
        if (rec.State != DraftState.Published) return rec;
        // Published versions are immutable; editing flips the composite back into an editable
        // working draft whose next Publish() appends a NEW version.
        rec.State = DraftState.Ready;
        rec.InterruptionReason = "已发布版本保持不可变；当前修改属于尚未发布的新草稿。";
        return rec;
    }

    public EditResult SetFrameExclusion(string compositeId, long revision, string frameId, bool excluded)
    {
        var current = _composites.Get<CompositeRecord>(compositeId);
        if (!current.FrameIds.Contains(frameId))
            throw new DomainException("FRAME_NOT_IN_COMPOSITE", $"该合成不包含帧 {frameId}。");
        if (current.State is DraftState.Publishing)
            throw new DomainException("INVALID_STATE", $"状态 {current.State} 下不能编辑。");

        var dirty = new List<int>();
        var rec = Mutate(compositeId, revision, r =>
        {
            BeginDraftIfPublished(r);
            bool changed = excluded ? r.ExcludedFrameIds.Add(frameId) : r.ExcludedFrameIds.Remove(frameId);
            if (changed)
            {
                foreach (var t in r.Tiles)
                {
                    bool covers = t.Scores.Any(s => s.FrameId == frameId && s.CoveredPixels > 0);
                    bool forced = t.ManualFrameId == frameId;
                    if (covers || forced)
                    {
                        t.State = TileState.Pending;
                        t.FailureReason = null;
                        if (forced) t.ManualFrameId = null;
                        dirty.Add(t.Index);
                    }
                }
                r.State = DraftState.Pending;
                r.InterruptionReason = "帧排除集合已变化，脏瓦片需要重新处理。";
            }
        });
        return new EditResult { Composite = rec, NeedsReprocessing = dirty.Count > 0, DirtyTiles = dirty };
    }

    public EditResult SetTileSource(string compositeId, long revision, int tileIndex, string? frameId)
    {
        var current = _composites.Get<CompositeRecord>(compositeId);
        if (current.State is DraftState.Publishing)
            throw new DomainException("INVALID_STATE", $"状态 {current.State} 下不能编辑。");
        if (tileIndex < 0 || tileIndex >= current.Tiles.Count)
            throw new DomainException("TILE_NOT_FOUND", $"瓦片 {tileIndex} 不存在。");
        BeginDraftIfPublished(current);
        var tile = current.Tiles[tileIndex];
        if (frameId != null)
        {
            if (!current.FrameIds.Contains(frameId))
                throw new DomainException("FRAME_NOT_IN_COMPOSITE", $"帧 {frameId} 不在合成中。");
            if (current.ExcludedFrameIds.Contains(frameId))
                throw new DomainException("FRAME_EXCLUDED", $"帧 {frameId} 已被排除，请先恢复。");
            if (!tile.Scores.Any(s => s.FrameId == frameId && s.CoveredPixels > 0))
                throw new DomainException("FRAME_DOES_NOT_COVER_TILE", $"帧 {frameId} 不覆盖瓦片 {tileIndex}，拒绝外推。");
        }

        var rec = Mutate(compositeId, revision, r =>
        {
            BeginDraftIfPublished(r);
            var t = r.Tiles[tileIndex];
            if (t.ManualFrameId != frameId)
            {
                t.ManualFrameId = frameId;
                t.State = TileState.Pending;
                t.FailureReason = null;
                r.State = DraftState.Pending;
                r.InterruptionReason = $"瓦片 {tileIndex} 来源已手工改选，需要重新处理。";
            }
        });
        return new EditResult { Composite = rec, NeedsReprocessing = true, DirtyTiles = new() { tileIndex } };
    }

    /// <summary>Reprocess only the pending (dirty) tiles, leaving validated tiles untouched.</summary>
    public CompositeRecord ReprocessDirty(string compositeId, CancellationToken ct = default)
    {
        var rec = _composites.Get<CompositeRecord>(compositeId);
        if (rec.State is DraftState.Published or DraftState.Publishing)
            throw new DomainException("INVALID_STATE", $"状态 {rec.State} 不可处理。");

        lock (LockFor(compositeId))
        {
            rec = _composites.Get<CompositeRecord>(compositeId);
            rec.State = DraftState.Processing;
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
            var compositor = new Compositor(new CompositorOptions { TileSize = rec.TileSize });
            var overrides = rec.Tiles.Where(t => t.ManualFrameId != null)
                .ToDictionary(t => t.Index, t => t.ManualFrameId!);

            var pending = rec.Tiles.Where(t => t.State == TileState.Pending).Select(t => t.Index).ToList();
            if (pending.Count == 0)
            {
                rec.State = rec.Tiles.All(t => t.State == TileState.Validated) ? DraftState.Ready : DraftState.Failed;
                Persist(rec);
                return rec;
            }

            var updated = new Dictionary<int, TileRecord>();
            foreach (var idx in pending)
            {
                ct.ThrowIfCancellationRequested();
                var result = compositor.Compose(plan, rec.ExcludedFrameIds, overrides, idx).Single();
                updated[idx] = new TileRecord
                {
                    Index = result.Index, X = result.X, Y = result.Y,
                    Width = result.Width, Height = result.Height,
                    Scores = result.Scores,
                    TrustPixelCounts = result.TrustPixelCounts,
                    ManualFrameId = overrides.GetValueOrDefault(result.Index),
                    State = result.AllNoCoverage ? TileState.Failed : TileState.Validated,
                    FailureReason = result.AllNoCoverage ? "TILE_NO_COVERAGE：该瓦片没有任何未排除帧覆盖，拒绝无声填充。" : null,
                };
            }
            foreach (var t in rec.Tiles)
                if (updated.TryGetValue(t.Index, out var nt)) t.CopyFrom(nt);

            rec.State = rec.Tiles.All(t => t.State == TileState.Validated) ? DraftState.Ready : DraftState.Failed;
            rec.InterruptionReason = rec.State == DraftState.Failed ? "仍有验证失败瓦片。" : null;
            Persist(rec);
            return rec;
        }
        catch (OperationCanceledException)
        {
            MarkCancelled(compositeId, "用户取消：脏瓦片重处理中断，未替换上一已发布版本。");
            throw;
        }
        catch (Exception ex)
        {
            MarkFailed(compositeId, "重处理异常：" + ex.Message);
            throw;
        }
    }

    public CompositeRecord Retry(string compositeId, CancellationToken ct = default) => Process(compositeId, ct);
}

internal static class TileRecordExtensions
{
    public static void CopyFrom(this TileRecord t, TileRecord o)
    {
        t.State = o.State;
        t.FailureReason = o.FailureReason;
        t.Scores = o.Scores;
        t.TrustPixelCounts = o.TrustPixelCounts;
        t.ManualFrameId = o.ManualFrameId;
    }
}

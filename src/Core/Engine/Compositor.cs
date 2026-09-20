using MicroStack.Core.Imaging;
using MicroStack.Core.Models;
using System.Text.Json;

namespace MicroStack.Core.Engine;

public sealed record RenderedArtifacts(
    byte[] CompositePng,
    byte[] OverlayPng,
    byte[] SourceMaskPng,
    string ScoresJson,
    string TransformJson);

public sealed class TileRenderInfo
{
    public required TileRec Tile { get; init; }
    public required Dictionary<string, double> MeanScores { get; init; }
    public required string? SelectedEvidenceId { get; init; }
    public required Dictionary<byte, int> UntrustedCounts { get; init; }
    public required int TrustedPixels { get; init; }
    public required List<string> CandidateEvidenceIds { get; init; }
}

public sealed class RenderResult(
    RenderedArtifacts artifacts,
    List<TileRenderInfo> tileInfos,
    Geometry.LayoutResult layout,
    byte[] sourceMaskRaw)
{
    public RenderedArtifacts Artifacts { get; } = artifacts;
    public List<TileRenderInfo> TileInfos { get; } = tileInfos;
    public Geometry.LayoutResult Layout { get; } = layout;
    public byte[] SourceMaskRaw { get; } = sourceMaskRaw;
}

/// <summary>Runs the deterministic focus-selection composition for one set of frames + overrides.</summary>
public sealed class Compositor
{
    private readonly EngineParameters _p;

    public Compositor(EngineParameters parameters) => _p = parameters;

    private readonly record struct Cover(int FrameIndex, FrameLayout Layout, int Sx, int Sy, bool Edge, bool Conflict);

    public RenderResult Render(
        IReadOnlyList<FrameRef> frames,
        IReadOnlyDictionary<string, PngImage> images,
        IReadOnlyList<TileRec>? priorTiles = null,
        IReadOnlyList<TileOverride>? overrides = null)
    {
        var layout = Geometry.BuildLayout(frames, _p);
        int cw = layout.CanvasWidth, ch = layout.CanvasHeight;
        double refUm = layout.ReferenceMagnification == 0
            ? _p.PixelScaleFactorUm
            : Geometry.PixelSizeUm(layout.ReferenceMagnification, _p);

        var appliedOverride = overrides?.ToDictionary(o => o.TileIndex) ?? new Dictionary<int, TileOverride>();
        var prior = priorTiles?.ToDictionary(t => t.Index) ?? new Dictionary<int, TileRec>();

        int tileCols = Math.Max(1, (cw + _p.TileSize - 1) / _p.TileSize);
        int tileRows = Math.Max(1, (ch + _p.TileSize - 1) / _p.TileSize);
        var tiles = new List<TileRec>();
        for (int ty = 0; ty < tileRows; ty++)
        for (int tx = 0; tx < tileCols; tx++)
        {
            int idx = ty * tileCols + tx;
            appliedOverride.TryGetValue(idx, out var ovr);
            var old = prior.GetValueOrDefault(idx);
            tiles.Add(new TileRec
            {
                Index = idx,
                X = tx * _p.TileSize,
                Y = ty * _p.TileSize,
                Width = Math.Min(_p.TileSize, cw - tx * _p.TileSize),
                Height = Math.Min(_p.TileSize, ch - ty * _p.TileSize),
                ExcludedEvidenceIds = ovr?.ExcludedEvidenceIds.OrderBy(x => x).ToList()
                                      ?? old?.ExcludedEvidenceIds ?? [],
                ForcedEvidenceId = ovr?.ForcedEvidenceId ?? old?.ForcedEvidenceId
            });
        }

        var frameIndex = new Dictionary<string, int>();
        for (int i = 0; i < frames.Count; i++) frameIndex[frames[i].EvidenceId] = i;

        var sharpness = new Dictionary<int, double[,]>();
        var scale = new Dictionary<int, double>();
        foreach (var l in layout.Layouts)
        {
            if (!l.Placed || !images.TryGetValue(l.EvidenceId, out var img)) continue;
            int fi = frameIndex[l.EvidenceId];
            sharpness[fi] = Sharpness.WindowAverage(
                Sharpness.TenengradMap(img), _p.SharpnessWindowRadius);
            scale[fi] = l.PixelSizeUm / refUm;
        }

        var composite = new byte[cw * ch];
        var overlay = new byte[cw * ch];
        var mask = new byte[cw * ch];
        Array.Fill(mask, MaskCodes.NoSource);

        var scoreSums = tiles.ToDictionary(t => t.Index, _ => new Dictionary<int, (double sum, long n)>());
        var selectedCounts = tiles.ToDictionary(t => t.Index, _ => new Dictionary<int, int>());
        var untrusted = tiles.ToDictionary(t => t.Index, _ => new Dictionary<byte, int>());
        var trusted = tiles.ToDictionary(t => t.Index, _ => 0);

        foreach (var t in tiles)
        {
            var excluded = t.ExcludedEvidenceIds.ToHashSet();
            int? forcedFi = t.ForcedEvidenceId != null ? frameIndex.GetValueOrDefault(t.ForcedEvidenceId) : null;
            for (int yy = t.Y; yy < t.Y + t.Height; yy++)
            for (int xx = t.X; xx < t.X + t.Width; xx++)
            {
                int pi = yy * cw + xx;
                var covers = Sample(xx, yy, images, layout, frameIndex, scale);
                bool conflictCover = covers.Any(c => c.Conflict);
                var usable = covers.Where(c => !c.Conflict &&
                    !excluded.Contains(frames[c.FrameIndex].EvidenceId)).ToList();

                if (conflictCover)
                {
                    // A magnification-conflicting frame projects here: never silently fill it
                    // (neither from the conflicting frame nor by hiding behind a good frame).
                    byte code = OverlayCodes.MagnificationConflict;
                    overlay[pi] = code;
                    Inc(untrusted[t.Index], code);
                    continue;
                }

                if (forcedFi.HasValue)
                {
                    var hit = usable.FirstOrDefault(c => c.FrameIndex == forcedFi.Value);
                    if (hit.Layout is not null)
                    {
                        WritePixel(composite, mask, overlay, pi, hit, forcedFi.Value, images);
                        Accumulate(scoreSums[t.Index], selectedCounts[t.Index], forcedFi.Value,
                            sharpness[forcedFi.Value][hit.Sx, hit.Sy]);
                        if (hit.Edge) Inc(untrusted[t.Index], OverlayCodes.ExtrapolatedEdge);
                        else trusted[t.Index]++;
                    }
                    else
                    {
                        overlay[pi] = OverlayCodes.NoCoverage;
                        Inc(untrusted[t.Index], OverlayCodes.NoCoverage);
                    }
                    continue;
                }

                var interior = usable.Where(c => !c.Edge).ToList();
                int alignedTotal = usable.Count;
                if (interior.Count > 0)
                {
                    var best = Best(interior, sharpness);
                    WritePixel(composite, mask, overlay, pi, best, best.FrameIndex, images);
                    Accumulate(scoreSums[t.Index], selectedCounts[t.Index], best.FrameIndex,
                        sharpness[best.FrameIndex][best.Sx, best.Sy]);
                    if (alignedTotal >= _p.MinimumAlignedFrames) trusted[t.Index]++;
                    else
                    {
                        overlay[pi] = OverlayCodes.InsufficientAlignment;
                        Inc(untrusted[t.Index], OverlayCodes.InsufficientAlignment);
                    }
                }
                else if (usable.Count > 0)
                {
                    var best = Best(usable, sharpness);
                    WritePixel(composite, mask, overlay, pi, best, best.FrameIndex, images);
                    Accumulate(scoreSums[t.Index], selectedCounts[t.Index], best.FrameIndex,
                        sharpness[best.FrameIndex][best.Sx, best.Sy]);
                    byte code = usable.Count >= _p.MinimumAlignedFrames
                        ? OverlayCodes.ExtrapolatedEdge
                        : OverlayCodes.InsufficientAlignment;
                    overlay[pi] = code;
                    Inc(untrusted[t.Index], code);
                }
                else
                {
                    overlay[pi] = OverlayCodes.NoCoverage;
                    Inc(untrusted[t.Index], OverlayCodes.NoCoverage);
                }
            }
        }

        var tileInfos = new List<TileRenderInfo>();
        foreach (var t in tiles)
        {
            var means = new Dictionary<string, double>();
            foreach (var l in layout.Layouts.Where(l => l.Placed && !l.MagnificationConflict))
            {
                int fi = frameIndex[l.EvidenceId];
                if (!sharpness.ContainsKey(fi)) continue;
                var (sum, n) = scoreSums[t.Index].GetValueOrDefault(fi);
                means[l.EvidenceId] = n == 0 ? FootprintSampleMean(sharpness[fi], l, refUm, t) : sum / n;
            }
            string? selected = null;
            if (selectedCounts[t.Index].Count > 0)
            {
                int fi = selectedCounts[t.Index]
                    .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
                selected = frames[fi].EvidenceId;
            }
            tileInfos.Add(new TileRenderInfo
            {
                Tile = t,
                MeanScores = means,
                SelectedEvidenceId = selected,
                UntrustedCounts = untrusted[t.Index],
                TrustedPixels = trusted[t.Index],
                CandidateEvidenceIds = means.Keys.OrderBy(k => frameIndex[k]).ToList()
            });
        }

        // Keep prior manual validation only when the effective frame choice inputs are identical.
        foreach (var info in tileInfos)
        {
            if (prior.TryGetValue(info.Tile.Index, out var old) && old.Validated &&
                old.ForcedEvidenceId == info.Tile.ForcedEvidenceId &&
                old.ExcludedEvidenceIds.SequenceEqual(info.Tile.ExcludedEvidenceIds))
            {
                info.Tile.Validated = true;
            }
        }

        var transform = new TransformDoc
        {
            RuleSetVersion = RuleSet.Version,
            CanvasWidth = cw,
            CanvasHeight = ch,
            ReferenceMagnification = layout.ReferenceMagnification,
            ReferencePixelSizeUm = refUm,
            Parameters = _p,
            Frames = frames.Select((f, i) =>
            {
                var l = layout.Layouts.FirstOrDefault(x => x.EvidenceId == f.EvidenceId);
                return new FrameTransform
                {
                    FrameIndex = i,
                    EvidenceId = f.EvidenceId,
                    ContentSha256 = f.ContentSha256,
                    Placed = l?.Placed ?? false,
                    Reason = l?.Reason,
                    MagnificationConflict = l?.MagnificationConflict ?? false,
                    PixelSizeUm = l?.PixelSizeUm ?? 0,
                    OriginCanvasX = l?.OriginX ?? 0,
                    OriginCanvasY = l?.OriginY ?? 0,
                    SourcePixelsPerCanvasPixel = l?.Placed == true ? l.PixelSizeUm / refUm : 0,
                    Width = f.Width,
                    Height = f.Height,
                    MagnificationMissing = f.MagnificationMissing,
                    Magnification = f.Magnification,
                    StageXUm = f.Stage.X,
                    StageYUm = f.Stage.Y,
                    StageXUnit = f.Stage.XUnit,
                    StageYUnit = f.Stage.YUnit,
                    ZHeightUm = f.ZHeightUm,
                    CapturedAt = f.CapturedAt
                };
            }).ToList()
        };

        var scoreDoc = new ScoresDoc
        {
            RuleSetVersion = RuleSet.Version,
            Parameters = _p,
            ReferenceMagnification = layout.ReferenceMagnification,
            Tiles = tileInfos.Select(i => new TileScores
            {
                TileIndex = i.Tile.Index,
                X = i.Tile.X,
                Y = i.Tile.Y,
                Width = i.Tile.Width,
                Height = i.Tile.Height,
                Validated = i.Tile.Validated,
                ForcedEvidenceId = i.Tile.ForcedEvidenceId,
                ExcludedEvidenceIds = i.Tile.ExcludedEvidenceIds,
                SelectedEvidenceId = i.SelectedEvidenceId,
                TrustedPixels = i.TrustedPixels,
                UntrustedPixels = i.UntrustedCounts.ToDictionary(kv => OverlayCodes.Names[kv.Key], kv => kv.Value),
                FrameScores = i.MeanScores.OrderBy(kv => frameIndex[kv.Key])
                    .Select(kv => new FrameScore { EvidenceId = kv.Key, MeanSharpness = kv.Value }).ToList()
            }).ToList()
        };

        var artifacts = new RenderedArtifacts(
            Png.Encode(composite, cw, ch, 1),
            Png.Encode(overlay, cw, ch, 1),
            Png.Encode(mask, cw, ch, 1),
            JsonSerializer.Serialize(scoreDoc, JsonOptions.Indented),
            JsonSerializer.Serialize(transform, JsonOptions.Indented));
        return new RenderResult(artifacts, tileInfos, layout, mask);
    }

    private List<Cover> Sample(
        int xx, int yy,
        IReadOnlyDictionary<string, PngImage> images,
        Geometry.LayoutResult layout,
        Dictionary<string, int> frameIndex,
        Dictionary<int, double> scale)
    {
        var result = new List<Cover>();
        foreach (var l in layout.Layouts)
        {
            if (!l.Placed || !images.TryGetValue(l.EvidenceId, out var img)) continue;
            int fi = frameIndex[l.EvidenceId];
            double s = scale[fi];
            int sx = (int)Math.Floor((xx - l.OriginX) / s);
            int sy = (int)Math.Floor((yy - l.OriginY) / s);
            if (sx < 0 || sy < 0 || sx >= img.Width || sy >= img.Height) continue;
            bool edge = sx < _p.EdgeGuardPixels || sy < _p.EdgeGuardPixels ||
                        sx >= img.Width - _p.EdgeGuardPixels || sy >= img.Height - _p.EdgeGuardPixels;
            result.Add(new Cover(fi, l, sx, sy, edge, l.MagnificationConflict));
        }
        return result;
    }

    private static void WritePixel(
        byte[] composite, byte[] mask, byte[] overlay, int pi, Cover c, int maskIndex,
        IReadOnlyDictionary<string, PngImage> images)
    {
        composite[pi] = images[c.Layout.EvidenceId]!.GrayAt(c.Sx, c.Sy);
        mask[pi] = (byte)maskIndex;
        overlay[pi] = c.Edge ? OverlayCodes.ExtrapolatedEdge : OverlayCodes.Trusted;
    }

    private static Cover Best(List<Cover> candidates, Dictionary<int, double[,]> sharpness) =>
        candidates
            .OrderByDescending(c => sharpness[c.FrameIndex]![c.Sx, c.Sy])
            .ThenBy(c => c.FrameIndex)
            .First();

    private static void Accumulate(Dictionary<int, (double, long)> sums, Dictionary<int, int> selected,
        int fi, double score)
    {
        var (s, n) = sums.GetValueOrDefault(fi);
        sums[fi] = (s + score, n + 1);
        selected[fi] = selected.GetValueOrDefault(fi) + 1;
    }

    private static void Inc(Dictionary<byte, int> d, byte code) =>
        d[code] = d.GetValueOrDefault(code) + 1;

    private static double FootprintSampleMean(double[,] map, FrameLayout l, double refUm, TileRec t)
    {
        double s = l.PixelSizeUm / refUm;
        double sum = 0; int n = 0;
        int w = map.GetLength(0), h = map.GetLength(1);
        for (int y = t.Y; y < t.Y + t.Height; y += 8)
        for (int x = t.X; x < t.X + t.Width; x += 8)
        {
            int sx = (int)((x - l.OriginX) / s), sy = (int)((y - l.OriginY) / s);
            if (sx < 0 || sy < 0 || sx >= w || sy >= h) continue;
            sum += map[sx, sy]; n++;
        }
        return n == 0 ? 0 : sum / n;
    }
}

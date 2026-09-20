using MicroFocus.Core.Imaging;

namespace MicroFocus.Core.Services;

public sealed class CompositorOptions
{
    public int TileSize { get; init; } = 64;
    public int SharpnessWindow { get; init; } = 7;
}

public sealed class TileCompositingResult
{
    public required int Index { get; init; }
    public required int X { get; init; }
    public required int Y { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Gray { get; init; }
    /// <summary>uint16 frame index into the plan frame list + 1; 0 = no coverage.</summary>
    public required ushort[] SourceIndex { get; init; }
    public required byte[] Trust { get; init; }
    public required List<TileFrameScore> Scores { get; init; }
    public required Dictionary<string, int> TrustPixelCounts { get; init; }
    public required bool AllNoCoverage { get; init; }
}

public sealed class Compositor
{
    public const string RuleVersion = "focus-stack-1.0";
    private const double RelativeEps = 1e-3;

    private readonly CompositorOptions _options;
    public Compositor(CompositorOptions? options = null) => _options = options ?? new CompositorOptions();

    public static int[,] TileGrid(int canvasWidth, int canvasHeight, int tileSize)
    {
        int cols = (canvasWidth + tileSize - 1) / tileSize;
        int rows = (canvasHeight + tileSize - 1) / tileSize;
        return new int[cols, rows];
    }

    public CompositorOptions Options => _options;

    public IEnumerable<TileCompositingResult> Compose(CanvasPlan plan, ISet<string> excludedFrameIds,
        IReadOnlyDictionary<int, string>? manualFrameByTile = null, int? onlyTileIndex = null,
        IProgress<int>? progress = null)
    {
        int tileSize = _options.TileSize;
        int cols = (plan.Width + tileSize - 1) / tileSize;
        int rows = (plan.Height + tileSize - 1) / tileSize;
        int total = cols * rows;

        var frames = plan.Frames
            .Select((pf, i) => new CompFrame
            {
                Index = i,
                Pf = pf,
                Excluded = excludedFrameIds.Contains(pf.Frame.Record.FrameId),
                Sharpness = BuildSharpness(pf.Frame.Image.Gray, pf.Frame.Image.Width, pf.Frame.Image.Height),
                SW = pf.Frame.Image.Width,
                SH = pf.Frame.Image.Height,
            })
            .ToList();

        int start = onlyTileIndex ?? 0;
        int end = onlyTileIndex is int one ? one + 1 : total;
        for (int t = start; t < end; t++)
        {
            int tx = (t % cols) * tileSize;
            int ty = (t / cols) * tileSize;
            int tw = Math.Min(tileSize, plan.Width - tx);
            int th = Math.Min(tileSize, plan.Height - ty);
            yield return ComposeTile(plan, frames, t, tx, ty, tw, th, manualFrameByTile?.GetValueOrDefault(t));
            progress?.Report((t - start + 1) * 100 / (end - start));
        }
    }

    private sealed class CompFrame
    {
        public int Index { get; init; }
        public PlannedFrame Pf { get; init; } = null!;
        public bool Excluded { get; set; }
        public long[] Sharpness { get; init; } = null!;
        public int SW { get; init; }
        public int SH { get; init; }
    }

    private TileCompositingResult ComposeTile(CanvasPlan plan, List<CompFrame> frames,
        int tileIndex, int tx, int ty, int tw, int th, string? manualFrameId)
    {
        int n = tw * th;
        var gray = new byte[n];
        var source = new ushort[n];
        var trust = new byte[n];
        var cover = new List<int>?[n];
        for (int i = 0; i < n; i++) cover[i] = new List<int>();

        var scoreRows = frames.Select(f => new TileFrameScore
        {
            FrameId = f.Pf.Frame.Record.FrameId,
            Excluded = f.Excluded,
        }).ToList();
        var sharpSums = new long[frames.Count];
        var sharpCounts = new int[frames.Count];

        for (int yy = 0; yy < th; yy++)
        {
            for (int xx = 0; xx < tw; xx++)
            {
                int cx = tx + xx, cy = ty + yy;
                var covering = cover[yy * tw + xx]!;
                foreach (var f in frames)
                {
                    var (fx, fy) = f.Pf.CanvasToPixel.Apply(cx + 0.5, cy + 0.5);
                    int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
                    if (ix >= 0 && iy >= 0 && ix < f.SW && iy < f.SH)
                    {
                        covering.Add(f.Index);
                        if (!f.Excluded)
                        {
                            sharpSums[f.Index] += f.Sharpness[iy * f.SW + ix];
                            sharpCounts[f.Index]++;
                        }
                    }
                }
            }
        }

        for (int fi = 0; fi < frames.Count; fi++)
        {
            scoreRows[fi].MeanSharpness = sharpCounts[fi] > 0 ? (double)sharpSums[fi] / sharpCounts[fi] : 0;
            scoreRows[fi].CoveredPixels = sharpCounts[fi];
        }

        // manual override resolution
        int? forced = null;
        if (manualFrameId != null)
        {
            int idx = frames.FindIndex(f => f.Pf.Frame.Record.FrameId == manualFrameId);
            if (idx >= 0) forced = idx;
        }

        var counts = new Dictionary<string, int>();
        long noCoverage = 0;

        for (int yy = 0; yy < th; yy++)
        {
            for (int xx = 0; xx < tw; xx++)
            {
                int pi = yy * tw + xx;
                int cx = tx + xx, cy = ty + yy;
                var covering = cover[pi]!;
                if (covering.Count == 0)
                {
                    trust[pi] = (byte)TrustCode.NoCoverage;
                    noCoverage++;
                    continue;
                }

                int chosen;
                if (forced is int fi && covering.Contains(fi) && !frames[fi].Excluded)
                {
                    chosen = fi;
                    scoreRows[fi].ChosenPixels++;
                    trust[pi] = (byte)TrustCode.ManualOverride;
                }
                else
                {
                    int best = -1;
                    long bestVal = long.MinValue;
                    foreach (var idx in covering)
                    {
                        if (frames[idx].Excluded) continue;
                        var f = frames[idx];
                        var (fx, fy) = f.Pf.CanvasToPixel.Apply(cx + 0.5, cy + 0.5);
                        int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
                        long val = f.Sharpness[iy * f.SW + ix];
                        if (val > bestVal) { bestVal = val; best = idx; }
                    }
                    if (best < 0)
                    {
                        trust[pi] = (byte)TrustCode.NoCoverage;
                        noCoverage++;
                        continue;
                    }
                    chosen = best;
                    scoreRows[best].ChosenPixels++;
                    trust[pi] = (byte)ClassifyTrust(covering, frames);
                }

                var chosenFrame = frames[chosen];
                var (px, py) = chosenFrame.Pf.CanvasToPixel.Apply(cx + 0.5, cy + 0.5);
                int u = Math.Clamp((int)Math.Floor(px), 0, chosenFrame.SW - 1);
                int v = Math.Clamp((int)Math.Floor(py), 0, chosenFrame.SH - 1);
                gray[pi] = chosenFrame.Pf.Frame.Image.Gray[v * chosenFrame.SW + u];
                source[pi] = (ushort)(chosen + 1);
                counts[((TrustCode)trust[pi]).ToString()] = counts.GetValueOrDefault(((TrustCode)trust[pi]).ToString()) + 1;
            }
        }

        bool allNoCoverage = noCoverage == n;
        if (forced.HasValue && manualFrameId != null)
        {
            var f = frames[forced.Value];
            scoreRows[forced.Value].ForcedOverride = true;
        }

        return new TileCompositingResult
        {
            Index = tileIndex,
            X = tx, Y = ty, Width = tw, Height = th,
            Gray = gray,
            SourceIndex = source,
            Trust = trust,
            Scores = scoreRows,
            TrustPixelCounts = counts,
            AllNoCoverage = allNoCoverage,
        };
    }

    private static TrustCode ClassifyTrust(List<int> covering, List<CompFrame> frames)
    {
        bool anyInferredScale = covering.Any(i => frames[i].Pf.ScaleInferred);
        // Magnification buckets: explicit values, or one implicit "unknown" bucket.
        var buckets = new List<double>();
        int unknown = 0;
        foreach (var i in covering)
        {
            var m = frames[i].Pf.Frame.Record.Magnification;
            if (m is > 0) buckets.Add(m.Value);
            else unknown++;
        }
        bool magConflict = buckets.Count >= 2 &&
            (buckets.Max() - buckets.Min()) / Math.Max(1e-9, Math.Abs(buckets.Min())) > RelativeEps;
        if (magConflict) return TrustCode.MagnificationConflict;
        int maxSameMag = buckets.Count == 0 ? 0 : buckets.GroupBy(m => m).Max(g => g.Count());
        maxSameMag += unknown; // frames without magnification can corroborate each other
        if (maxSameMag < 2) return TrustCode.SingleFrame;
        if (anyInferredScale) return TrustCode.ScaleMissing;
        return TrustCode.Trusted;
    }

    /// <summary>
    /// Windowed sum of squared Laplacian responses via an integral image:
    /// energy(p) = sum over window of (4*g - gL - gR - gU - gD)^2.
    /// </summary>
    public static long[] BuildSharpness(byte[] gray, int w, int h)
    {
        const int radius = 3; // 7x7 window
        var lap = new long[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                int c = gray[i];
                int l = x > 0 ? gray[i - 1] : c;
                int r = x < w - 1 ? gray[i + 1] : c;
                int u = y > 0 ? gray[i - w] : c;
                int d = y < h - 1 ? gray[i + w] : c;
                long v = 4L * c - l - r - u - d;
                lap[i] = v * v;
            }
        }
        var integral = new long[(long)(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            long rowSum = 0;
            for (int x = 0; x < w; x++)
            {
                rowSum += lap[y * w + x];
                integral[(long)(y + 1) * (w + 1) + (x + 1)] =
                    integral[(long)y * (w + 1) + (x + 1)] + rowSum;
            }
        }
        var result = new long[w * h];
        for (int y = 0; y < h; y++)
        {
            int y0 = Math.Max(0, y - radius), y1 = Math.Min(h - 1, y + radius);
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - radius), x1 = Math.Min(w - 1, x + radius);
                result[y * w + x] =
                    integral[(long)(y1 + 1) * (w + 1) + (x1 + 1)]
                    - integral[(long)y0 * (w + 1) + (x1 + 1)]
                    - integral[(long)(y1 + 1) * (w + 1) + x0]
                    + integral[(long)y0 * (w + 1) + x0];
            }
        }
        return result;
    }
}

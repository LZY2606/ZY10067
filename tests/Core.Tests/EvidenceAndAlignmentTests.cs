using MicroFocus.Core.Services;
using MicroFocus.Core.Storage;
using Xunit;

namespace MicroFocus.Core.Tests;

public class EvidenceAndAlignmentTests
{
    [Fact]
    public void Evidence_IsImmutable_AndContentAddressed_Deduplicates()
    {
        using var h = new ServiceHarness();
        var job = h.NewJob();
        var bytes = TestData.FlatFrame(32, 32, 80);
        var f1 = h.AddFrame(job.JobId, bytes, "a.pgm");
        var f2 = h.AddFrame(job.JobId, bytes, "same-bytes-different-name.pgm");
        Assert.Equal(f1.FrameId, f2.FrameId);
        Assert.Equal(f1.Sha256, f2.Sha256);

        var storedPath = h.Jobs.Evidence.ResolvePath(f1.Sha256);
        var before = File.GetLastWriteTimeUtc(storedPath);
        Assert.True(h.Jobs.Evidence.Verify(f1.Sha256));

        // Tampering with evidence on disk is detected by hash verification.
        var raw = File.ReadAllBytes(storedPath);
        raw[^1] ^= 0xFF;
        File.WriteAllBytes(storedPath, raw);
        Assert.False(h.Jobs.Evidence.Verify(f1.Sha256));
    }

    [Fact]
    public void SameName_ReuploadWithDifferentBytes_CreatesNewFrame_AndVersionKeepsOldFingerprint()
    {
        using var h = new ServiceHarness();
        var job = h.NewJob();
        var f1 = h.AddFrame(job.JobId, TestData.FlatFrame(40, 40, 60), "view1.pgm",
            sx: 0, sy: 0, z: 0);
        var f2 = h.AddFrame(job.JobId, TestData.FlatFrame(40, 40, 120), "view1.pgm",
            sx: 0, sy: 0, z: 1);
        Assert.NotEqual(f1.FrameId, f2.FrameId);
        Assert.NotEqual(f1.Sha256, f2.Sha256);
        Assert.Equal(2, h.Jobs.ListFrames(job.JobId).Count);
    }

    [Fact]
    public void MissingStageUnits_ExcludesFrame_FromAlignment()
    {
        using var h = new ServiceHarness();
        var job = h.NewJob();
        h.AddFrame(job.JobId, TestData.FlatFrame(48, 48), "good.pgm", sx: 0, sy: 0);
        h.AddFrame(job.JobId, TestData.FlatFrame(48, 48), "no-units.pgm", unit: "", sx: null, sy: null);
        var comp = h.Composites.CreateComposite(job.JobId, "c");
        var processed = h.Composites.Process(comp.CompositeId);
        Assert.Contains(processed.ExcludedFromGeometryFrameIds, id => h.Jobs.GetFrame(id).OriginalFileName == "no-units.pgm");
        Assert.Contains(processed.Diagnostics, d => d.Code == "STAGE_UNIT_MISSING");
    }

    [Fact]
    public void NoAlignableFrames_FailsCandidate_WithoutSilentStretch()
    {
        using var h = new ServiceHarness();
        var job = h.NewJob();
        h.AddFrame(job.JobId, TestData.FlatFrame(32, 32), "x.pgm", unit: "", sx: null, sy: null);
        var comp = h.Composites.CreateComposite(job.JobId, "c");
        var processed = h.Composites.Process(comp.CompositeId);
        Assert.Equal(DraftState.Failed, processed.State);
        Assert.Null(processed.CanvasWidth);
        Assert.Contains(processed.Diagnostics, d => d.Code == "NO_ALIGNABLE_FRAME");
    }

    [Fact]
    public void ReshootFrame_PartialCoverage_MarksUnionEdges_AsSingleFrame_NotExtrapolated()
    {
        using var h = new ServiceHarness();
        var job = h.NewJob();
        // primary covers 128x128 stage region at 0.32um (40x40 px)
        h.AddFrame(job.JobId, TestData.DetailFrame(40, 40, 5, 5, 12), "f0.pgm", sx: 0, sy: 0, z: 0);
        h.AddFrame(job.JobId, TestData.DetailFrame(40, 40, 20, 20, 12, 1), "f1.pgm", sx: 0, sy: 0, z: 1);
        // reshoot covers only a small stage patch offset to the side
        h.AddFrame(job.JobId, TestData.DetailFrame(20, 20, 2, 2, 10, 2), "reshoot.pgm",
            sx: 8.0, sy: 8.0, z: 1, role: FrameRole.Reshoot);

        var comp = h.Composites.CreateComposite(job.JobId, "c");
        var processed = h.Composites.Process(comp.CompositeId);
        Assert.Equal(DraftState.Ready, processed.State);
        // union canvas extends beyond primary to cover the reshoot; no frame => impossible inside union.
        Assert.True(processed.CanvasWidth >= (int)Math.Ceiling((8.0 + 20 * 0.32) / 0.32) - 1,
            $"canvas {processed.CanvasWidth}");
        // reshoot-only area is single-frame trust
        var reshootId = h.Jobs.ListFrames(job.JobId).Single(f => f.Role == FrameRole.Reshoot).FrameId;
        bool hasSingle = processed.Tiles.Any(t =>
            t.Scores.Any(sc => sc.FrameId == reshootId && sc.CoveredPixels > 0)
            && t.TrustPixelCounts.TryGetValue("SingleFrame", out var n) && n > 0);
        if (!hasSingle)
        {
            foreach (var t in processed.Tiles)
                Console.WriteLine($"tile {t.Index} covers reshoot=" +
                    $"{t.Scores.FirstOrDefault(x => x.FrameId == reshootId)?.CoveredPixels} trust=" +
                    string.Join(",", t.TrustPixelCounts.Select(kv => kv.Key + "=" + kv.Value)));
        }
        Assert.True(hasSingle);
        // there must be no NoCoverage pixels because canvas is exactly the union
        foreach (var t in processed.Tiles)
        {
            t.TrustPixelCounts.TryGetValue("NoCoverage", out var nc);
            Assert.Equal(0, nc);
        }
    }

    [Fact]
    public void MagnificationConflict_IsDiagnosed_AndOverlapMarkedUntrusted()
    {
        using var h = new ServiceHarness();
        var job = h.NewJob();
        h.AddFrame(job.JobId, TestData.FlatFrame(40, 40), "lo.pgm", mag: 10, pixelSize: 0.65, sx: 0, sy: 0);
        h.AddFrame(job.JobId, TestData.FlatFrame(40, 40), "hi.pgm", mag: 40, pixelSize: 0.16, sx: 0, sy: 0);
        var comp = h.Composites.CreateComposite(job.JobId, "c");
        var processed = h.Composites.Process(comp.CompositeId);
        Assert.Contains(processed.Diagnostics, d => d.Code == "MAGNIFICATION_CONFLICT");
        bool hasConflict = processed.Tiles.Any(t =>
            t.TrustPixelCounts.TryGetValue("MagnificationConflict", out var n) && n > 0);
        Assert.True(hasConflict);
    }
}

using MicroFocus.Core.Export;
using MicroFocus.Core.Storage;
using Xunit;

namespace MicroFocus.Core.Tests;

public class CompositeLifecycleTests
{
    private static ServiceHarness StackWithTwoFocusFrames(out JobRecord job, out FrameRecord near, out FrameRecord far)
    {
        var h = new ServiceHarness();
        job = h.NewJob();
        // Two frames, same field: one sharp in left patch, one sharp in right patch.
        var imgA = TestData.Pgm(64, 64, (x, y) =>
        {
            int v = 50 + (x + y) % 11;
            if (x < 28 && ((x + y) & 3) < 2) v = 220;
            return (byte)v;
        });
        var imgB = TestData.Pgm(64, 64, (x, y) =>
        {
            int v = 50 + (x + y) % 11;
            if (x >= 36 && ((x + y) & 3) < 2) v = 220;
            return (byte)v;
        });
        near = h.AddFrame(job.JobId, imgA, "near.pgm", sx: 0, sy: 0, z: 0);
        far = h.AddFrame(job.JobId, imgB, "far.pgm", sx: 0, sy: 0, z: 2);
        return h;
    }

    [Fact]
    public void Process_ValidatesEveryTile_AndPublishingRequiresAllValidated()
    {
        using var h = StackWithTwoFocusFrames(out var job, out _, out _);
        var comp = h.Composites.CreateComposite(job.JobId, "c");
        var processed = h.Composites.Process(comp.CompositeId);
        Assert.True(processed.Tiles.Count > 1);
        Assert.All(processed.Tiles, t => Assert.Equal(TileState.Validated, t.State));
        Assert.Equal(DraftState.Ready, processed.State);

        var pub = h.Composites.Publish(comp.CompositeId, processed.Revision, "first");
        Assert.Equal(DraftState.Published, pub.Composite.State);
        Assert.Single(h.Composites.ListVersions(comp.CompositeId));
        Assert.True(File.Exists(Path.Combine(new DerivativeStore(h.Dir).RootPhysical,
            comp.CompositeId, pub.Version.VersionId, "composite.pgm")));
    }

    [Fact]
    public void ExcludingAllCoveringFrames_FailsTiles_AndPublishIsRefused()
    {
        using var h = StackWithTwoFocusFrames(out var job, out var near, out var far);
        var comp = h.Composites.CreateComposite(job.JobId, "c");
        var processed = h.Composites.Process(comp.CompositeId);
        long rev = processed.Revision;
        processed = h.Composites.SetFrameExclusion(comp.CompositeId, rev, near.FrameId, true).Composite;
        processed = h.Composites.SetFrameExclusion(comp.CompositeId, processed.Revision, far.FrameId, true).Composite;
        var redone = h.Composites.ReprocessDirty(comp.CompositeId);
        Assert.Contains(redone.Tiles, t => t.State == TileState.Failed);
        var ex = Assert.Throws<DomainException>(() =>
            h.Composites.Publish(comp.CompositeId, redone.Revision, "bad"));
        Assert.Equal("TILES_NOT_VALIDATED", ex.Code);
    }

    [Fact]
    public void Cancel_DoesNotReplace_PreviousPublishedVersion()
    {
        using var h = StackWithTwoFocusFrames(out var job, out _, out _);
        var comp = h.Composites.CreateComposite(job.JobId, "c");
        var p0 = h.Composites.Process(comp.CompositeId);
        var v1 = h.Composites.Publish(comp.CompositeId, p0.Revision, "v1");

        // Exclude a frame -> dirty draft, then cancel before reprocessing.
        var edited = h.Composites.SetFrameExclusion(comp.CompositeId, v1.Composite.Revision,
            v1.Version.Fingerprints[0].FrameId, true).Composite;
        h.Composites.Cancel(comp.CompositeId, edited.Revision);

        var after = h.Composites.GetComposite(comp.CompositeId);
        Assert.Equal(DraftState.Cancelled, after.State);
        Assert.Equal(v1.Version.VersionId, after.PublishedVersionId);
        var versions = h.Composites.ListVersions(comp.CompositeId);
        Assert.Single(versions);
        Assert.Equal(v1.Version.Sha256Safe(), versions[0].Sha256Safe());
    }

    [Fact]
    public void StaleRevision_IsRejected_WithCurrentRevision()
    {
        using var h = StackWithTwoFocusFrames(out var job, out var near, out _);
        var comp = h.Composites.CreateComposite(job.JobId, "c");
        var processed = h.Composites.Process(comp.CompositeId);
        long stale = processed.Revision;
        h.Composites.SetFrameExclusion(comp.CompositeId, stale, near.FrameId, true);
        var ex = Assert.Throws<ConflictException>(() =>
            h.Composites.SetFrameExclusion(comp.CompositeId, stale, near.FrameId, false));
        Assert.Equal(stale + 1, ex.CurrentRevision);
    }

    [Fact]
    public void ManualTileOverride_IsStoredOnTile_AndMarksManualTrust()
    {
        using var h = StackWithTwoFocusFrames(out var job, out var near, out var far);
        var comp = h.Composites.CreateComposite(job.JobId, "c");
        var processed = h.Composites.Process(comp.CompositeId);
        // force tile 0 to the second frame
        var edited = h.Composites.SetTileSource(comp.CompositeId, processed.Revision, 0, far.FrameId).Composite;
        var redone = h.Composites.ReprocessDirty(comp.CompositeId);
        Assert.Equal(far.FrameId, redone.Tiles[0].ManualFrameId);
        Assert.True(redone.Tiles[0].TrustPixelCounts.ContainsKey("ManualOverride"));
        Assert.All(redone.Tiles.Skip(1), t => Assert.Null(t.ManualFrameId));
    }

    [Fact]
    public void OverrideToFrameNotCoveringTile_IsRejected_NoExtrapolation()
    {
        using var h = new ServiceHarness();
        var job = h.NewJob();
        h.AddFrame(job.JobId, TestData.FlatFrame(40, 40), "a.pgm", sx: 0, sy: 0, z: 0);
        var off = h.AddFrame(job.JobId, TestData.FlatFrame(40, 40), "far-away.pgm",
            sx: 500, sy: 500, z: 1);
        var comp = h.Composites.CreateComposite(job.JobId, "c");
        var processed = h.Composites.Process(comp.CompositeId);
        var targetTile = processed.Tiles.First(t =>
            !t.Scores.Any(s => s.FrameId == off.FrameId && s.CoveredPixels > 0));
        var ex = Assert.Throws<DomainException>(() =>
            h.Composites.SetTileSource(comp.CompositeId, processed.Revision, targetTile.Index, off.FrameId));
        Assert.Equal("FRAME_DOES_NOT_COVER_TILE", ex.Code);
    }
}

internal static class VersionTestExtensions
{
    public static string Sha256Safe(this VersionRecord v) => v.VersionId + v.PublishedAt.Ticks;
}

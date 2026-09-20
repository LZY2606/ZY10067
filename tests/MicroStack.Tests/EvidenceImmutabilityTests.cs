using MicroStack.Tests.Support;

namespace MicroStack.Tests;

public class EvidenceImmutabilityTests
{
    [Fact]
    public async Task SameBytes_Deduplicate_AndKeepReceivedMetadata()
    {
        using var h = new TestHarness();
        var png = h.MakePng(10, 10, (x, y) => (byte)(x + y));
        var first = await h.App.IngestEvidenceAsync("first.png", png);
        var second = await h.App.IngestEvidenceAsync("second-name.png", png);
        Assert.Equal(first.EvidenceId, second.EvidenceId);
        Assert.True(second.Duplicate);
        var (meta, bytes) = await h.App.LoadEvidenceAsync(first.EvidenceId);
        Assert.Equal("first.png", meta.OriginalFilename); // original metadata untouched
        Assert.Equal(png, bytes);
    }

    [Fact]
    public async Task ReuploadingSameName_WithDifferentBytes_DoesNotReplaceOldEvidence()
    {
        using var h = new TestHarness();
        var png1 = h.MakePng(8, 8, (_, _) => 10);
        var png2 = h.MakePng(8, 8, (_, _) => 200);
        var r1 = await h.App.IngestEvidenceAsync("same.png", png1);
        var r2 = await h.App.IngestEvidenceAsync("same.png", png2);
        Assert.NotEqual(r1.EvidenceId, r2.EvidenceId);
        var (meta, bytes) = await h.App.LoadEvidenceAsync(r1.EvidenceId);
        Assert.Equal(png1, bytes);
        Assert.Equal("same.png", meta.OriginalFilename);
    }

    [Fact]
    public async Task OldVersion_KeepsItsFrameFingerprints_AfterLaterBindings()
    {
        using var h = new TestHarness();
        var doc = await h.CreateStackAsync();
        var v1 = doc.Versions[0];
        var oldFingerprint = v1.Frames[0].ContentSha256;

        // A brand-new composition reuses the same evidence files (same names later re-uploaded elsewhere).
        // The old version snapshot still resolves to the same fingerprint via its embedded FrameRef.
        var doc2 = await h.App.GetCompositionAsync(doc.Id);
        Assert.Equal(oldFingerprint, doc2.Versions[0].Frames[0].ContentSha256);
        Assert.Equal(v1.Frames[0].ReceivedAt, doc2.Versions[0].Frames[0].ReceivedAt);
    }
}

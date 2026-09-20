using MicroStack.Core.Models;
using MicroStack.Core.Services;
using MicroStack.Tests.Support;

namespace MicroStack.Tests;

public class WorkflowTests
{
    [Fact]
    public async Task InitialCandidate_CarriesRuleVersionParametersScoresAndTransforms()
    {
        using var h = new TestHarness();
        var doc = await h.CreateStackAsync();
        var v = doc.Versions[0];

        Assert.Equal(RuleSet.Version, v.RuleSetVersion);
        Assert.True(v.Tiles.Count > 0);
        var scores = System.Text.Encoding.UTF8.GetString(
            await h.App.GetArtifactAsync(doc.Id, v.VersionId, "scores"));
        var transforms = System.Text.Encoding.UTF8.GetString(
            await h.App.GetArtifactAsync(doc.Id, v.VersionId, "transforms"));
        Assert.Contains(RuleSet.Version, scores);
        Assert.Contains("originCanvasX", transforms);
        Assert.Contains("contentSha256", transforms);
    }

    [Fact]
    public async Task Publish_RequiresEveryTileValidated_AndIsAtomic()
    {
        using var h = new TestHarness();
        var doc = await h.CreateStackAsync();
        var v0 = doc.Versions[0];

        var ex = await Assert.ThrowsAsync<ServiceException>(() =>
            h.App.PublishAsync(doc.Id, new PublishRequest { VersionId = v0.VersionId }));
        Assert.Equal(409, ex.StatusCode);
        Assert.Null((await h.App.GetCompositionAsync(doc.Id)).PublishedVersionId);

        foreach (var t in v0.Tiles)
            await h.App.SetTileValidationAsync(doc.Id, v0.VersionId,
                new TileValidationRequest { TileIndex = t.Index, Validated = true });
        var pub = await h.App.PublishAsync(doc.Id, new PublishRequest { VersionId = v0.VersionId });
        Assert.Equal(v0.VersionId, pub.PublishedVersionId);
    }

    [Fact]
    public async Task TileExclusion_ChangesSelection_AndInvalidatesChangedTilesOnly()
    {
        using var h = new TestHarness();
        var doc = await h.CreateStackAsync();
        var v0 = doc.Versions[0];
        await h.App.SetTileValidationAsync(doc.Id, v0.VersionId,
            new TileValidationRequest { TileIndex = 0, Validated = true });
        await h.App.SetTileValidationAsync(doc.Id, v0.VersionId,
            new TileValidationRequest { TileIndex = 1, Validated = true });

        var dustId = v0.Frames[1].EvidenceId;
        var outcome = await h.App.CreateVersionAsync(doc.Id, new CreateVersionRequest
        {
            BaseVersionId = v0.VersionId,
            Note = "exclude dust on tile 0",
            Operations =
            [
                new TileOperation { TileIndex = 0, Kind = "replace", ExcludedEvidenceIds = [dustId] }
            ]
        });
        var v1 = outcome.Version;
        Assert.False(v1.Tiles[0].Validated);  // changed tile needs re-validation
        Assert.True(v1.Tiles[1].Validated);   // untouched tile keeps validation
        Assert.Contains(dustId, v1.Tiles[0].ExcludedEvidenceIds);
    }

    [Fact]
    public async Task CancelCandidate_DoesNotReplacePublishedVersion()
    {
        using var h = new TestHarness();
        var doc = await h.CreateStackAsync();
        var v0 = doc.Versions[0];
        foreach (var t in v0.Tiles)
            await h.App.SetTileValidationAsync(doc.Id, v0.VersionId,
                new TileValidationRequest { TileIndex = t.Index, Validated = true });
        await h.App.PublishAsync(doc.Id, new PublishRequest { VersionId = v0.VersionId });

        var next = await h.App.CreateVersionAsync(doc.Id, new CreateVersionRequest
        {
            BaseVersionId = v0.VersionId,
            Operations =
            [
                new TileOperation { TileIndex = 0, Kind = "replace", ForcedEvidenceId = v0.Frames[2].EvidenceId }
            ]
        });
        await h.App.CancelVersionAsync(doc.Id, next.Version.VersionId);
        var after = await h.App.GetCompositionAsync(doc.Id);
        Assert.Equal(v0.VersionId, after.PublishedVersionId);
        Assert.DoesNotContain(after.Versions, x => x.VersionId == next.Version.VersionId);
    }
}

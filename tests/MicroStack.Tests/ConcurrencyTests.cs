using MicroStack.Core.Models;
using MicroStack.Core.Services;
using MicroStack.Tests.Support;

namespace MicroStack.Tests;

public class ConcurrencyTests
{
    [Fact]
    public async Task StaleSubmissions_OnDisjointTiles_AutoMerge()
    {
        using var h = new TestHarness();
        var doc = await h.CreateStackAsync(size: 96, tileSize: 48);
        var v0 = doc.Versions[0];
        Assert.True(v0.Tiles.Count >= 2);

        // Browser A commits tile 0.
        var a = await h.App.CreateVersionAsync(doc.Id, new CreateVersionRequest
        {
            BaseVersionId = v0.VersionId,
            Operations = [new TileOperation { TileIndex = 0, Kind = "replace", ExcludedEvidenceIds = [v0.Frames[0].EvidenceId] }]
        });
        Assert.False(a.AutoMerged);

        // Browser B still holds v0, edits a different tile -> auto merge.
        var b = await h.App.CreateVersionAsync(doc.Id, new CreateVersionRequest
        {
            BaseVersionId = v0.VersionId,
            Operations = [new TileOperation { TileIndex = 1, Kind = "replace", ExcludedEvidenceIds = [v0.Frames[2].EvidenceId] }]
        });
        Assert.True(b.AutoMerged);
        Assert.Contains(v0.Frames[0].EvidenceId, b.Version.Tiles[0].ExcludedEvidenceIds);
        Assert.Contains(v0.Frames[2].EvidenceId, b.Version.Tiles[1].ExcludedEvidenceIds);
    }

    [Fact]
    public async Task StaleSubmissions_OnSameTile_Return409WithBothContents()
    {
        using var h = new TestHarness();
        var doc = await h.CreateStackAsync();
        var v0 = doc.Versions[0];

        await h.App.CreateVersionAsync(doc.Id, new CreateVersionRequest
        {
            BaseVersionId = v0.VersionId,
            Operations = [new TileOperation { TileIndex = 0, Kind = "replace", ExcludedEvidenceIds = [v0.Frames[0].EvidenceId] }]
        });

        var ex = await Assert.ThrowsAsync<ServiceException>(() =>
            h.App.CreateVersionAsync(doc.Id, new CreateVersionRequest
            {
                BaseVersionId = v0.VersionId,
                Operations = [new TileOperation { TileIndex = 0, Kind = "replace", ExcludedEvidenceIds = [v0.Frames[2].EvidenceId] }]
            }));
        Assert.Equal(409, ex.StatusCode);
        var conflict = Assert.IsType<ConflictResult>(ex.Payload);
        Assert.Single(conflict.Conflicts);
        Assert.Contains(v0.Frames[0].EvidenceId, conflict.Conflicts[0].ServerExcludedEvidenceIds);
        Assert.Contains(v0.Frames[2].EvidenceId, conflict.Conflicts[0].ClientExcludedEvidenceIds);

        // Client re-merges onto server version, choosing server tile + adding its edit elsewhere.
        var merged = await h.App.CreateVersionAsync(doc.Id, new CreateVersionRequest
        {
            BaseVersionId = conflict.CurrentVersionId,
            Operations = [new TileOperation { TileIndex = 1, Kind = "replace", ExcludedEvidenceIds = [v0.Frames[1].EvidenceId] }]
        });
        Assert.Contains(v0.Frames[0].EvidenceId, merged.Version.Tiles[0].ExcludedEvidenceIds);
    }
}

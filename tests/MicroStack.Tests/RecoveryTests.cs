using MicroStack.Core.Services;
using MicroStack.Tests.Support;

namespace MicroStack.Tests;

public class RecoveryTests
{
    [Fact]
    public async Task Startup_RemovesStagingFiles_ButKeepsPublishedState()
    {
        using var h = new TestHarness();
        var doc = await h.CreateStackAsync();
        var v = doc.Versions[0];
        foreach (var t in v.Tiles)
            await h.App.SetTileValidationAsync(doc.Id, v.VersionId,
                new TileValidationRequest { TileIndex = t.Index, Validated = true });
        await h.App.PublishAsync(doc.Id, new PublishRequest { VersionId = v.VersionId });

        // Simulate a hard crash in the middle of writing a document and an object.
        string dir = h.Dir;
        await File.WriteAllTextAsync(Path.Combine(dir, "compositions", "cmp_x.json.tmp-deadbeef"), "{ broken");
        string objectDir = Path.Combine(dir, "objects");
        Directory.CreateDirectory(objectDir);
        await File.WriteAllTextAsync(Path.Combine(objectDir, "dead.tmp-cafebabe"), "partial");

        // New process starts against the same directory.
        var restarted = new AppService(dir, new MicroStack.Core.Storage.LocalObjectStore(objectDir));
        var after = await restarted.GetCompositionAsync(doc.Id);
        Assert.Equal(v.VersionId, after.PublishedVersionId);
        Assert.Empty(Directory.GetFiles(dir, "*.tmp-*", SearchOption.AllDirectories));
    }
}

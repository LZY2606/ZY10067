using System.IO.Compression;
using MicroStack.Core.Exchange;
using MicroStack.Core.Models;
using MicroStack.Core.Services;
using MicroStack.Core.Storage;
using MicroStack.Tests.Support;

namespace MicroStack.Tests;

public class PackageExchangeTests
{
    private static PackageService Packages(TestHarness h) =>
        new(h.App, new LocalObjectStore(Path.Combine(h.Dir, "objects")));

    [Fact]
    public async Task RoundTrip_ExportThenVerify_PerPixelIdentical()
    {
        using var h = new TestHarness();
        var doc = await h.CreateStackAsync(size: 96, tileSize: 32);
        var v = doc.Versions[0];
        foreach (var t in v.Tiles)
            await h.App.SetTileValidationAsync(doc.Id, v.VersionId,
                new TileValidationRequest { TileIndex = t.Index, Validated = true });
        await h.App.PublishAsync(doc.Id, new PublishRequest { VersionId = v.VersionId });

        var pkg = await Packages(h).ExportAsync(doc.Id, v.VersionId);
        var report = await Packages(h).VerifyPackageAsync(pkg);

        Assert.True(report.Verified, string.Join("; ", report.Issues.Select(i => i.Message)));
        Assert.Equal(0, report.MismatchedPixels);
        Assert.Equal(96 * 96, report.PixelsChecked);
        Assert.Equal(report.CompositeSha256, report.RecomputedSha256);
    }

    [Fact]
    public async Task TamperedComposite_IsDetectedPixelByPixel()
    {
        using var h = new TestHarness();
        var doc = await h.CreateStackAsync();
        var v = doc.Versions[0];
        var pkg = await Packages(h).ExportAsync(doc.Id, v.VersionId);

        var tampered = ReplaceEntry(pkg, "composite.png", MutatePng(
            ExtractEntry(pkg, "composite.png")));
        var report = await Packages(h).VerifyPackageAsync(tampered);
        Assert.False(report.Verified);
        Assert.True(report.MismatchedPixels >= 1);
        Assert.Contains(report.Issues, i => i.Code == "pixel-mismatch");
    }

    [Fact]
    public async Task TamperedEvidence_IsDetectedByHash()
    {
        using var h = new TestHarness();
        var doc = await h.CreateStackAsync();
        var v = doc.Versions[0];
        var pkg = await Packages(h).ExportAsync(doc.Id, v.VersionId);

        var manifest = ReadJson<ExportManifest>(pkg, "manifest.json");
        var ev0 = manifest.Frames[0].EvidenceFile;
        var tampered = ReplaceEntry(pkg, ev0, MutatePng(ExtractEntry(pkg, ev0)));
        var report = await Packages(h).VerifyPackageAsync(tampered);
        Assert.False(report.Verified);
        Assert.Contains(report.Issues, i => i.Code == "evidence-hash-mismatch");
    }

    private static byte[] ExtractEntry(byte[] zipBytes, string name)
    {
        using var ms = new MemoryStream(zipBytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        using var s = zip.GetEntry(name)!.Open();
        using var o = new MemoryStream();
        s.CopyTo(o);
        return o.ToArray();
    }

    private static byte[] ReplaceEntry(byte[] zipBytes, string target, byte[] content)
    {
        using var inMs = new MemoryStream(zipBytes);
        using var src = new ZipArchive(inMs, ZipArchiveMode.Read);
        var entries = src.Entries.Select(e => (Name: e.FullName.Replace('\\', '/'), Bytes: ReadEntryBytes(e))).ToList();
        using var outMs = new MemoryStream();
        using (var dst = new ZipArchive(outMs, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                var e = dst.CreateEntry(name, CompressionLevel.NoCompression);
                using var es = e.Open();
                es.Write(name == target ? content : bytes);
            }
        }
        return outMs.ToArray();
    }

    private static byte[] ReadEntryBytes(ZipArchiveEntry entry)
    {
        using var rs = entry.Open();
        using var o = new MemoryStream();
        rs.CopyTo(o);
        return o.ToArray();
    }

    private static byte[] MutatePng(byte[] png)
    {
        var img = MicroStack.Core.Imaging.Png.Decode(png);
        // overwrite the last 16x16 block with a solid block: deterministic pixel mismatch
        for (int y = img.Height - 16; y < img.Height; y++)
        for (int x = img.Width - 16; x < img.Width; x++)
            img.SetGray(x, y, (byte)(img.GrayAt(x, y) ^ 0x5A));
        return MicroStack.Core.Imaging.Png.Encode(img);
    }

    private static T ReadJson<T>(byte[] zipBytes, string name)
    {
        using var ms = new MemoryStream(zipBytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        using var s = zip.GetEntry(name)!.Open();
        using var sr = new StreamReader(s);
        return System.Text.Json.JsonSerializer.Deserialize<T>(sr.ReadToEnd(),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}

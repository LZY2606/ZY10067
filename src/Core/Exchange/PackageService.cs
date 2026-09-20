using MicroStack.Core.Engine;
using MicroStack.Core.Imaging;
using MicroStack.Core.Models;
using MicroStack.Core.Services;
using MicroStack.Core.Storage;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MicroStack.Core.Exchange;

public sealed class PackageService(AppService app, IObjectStore objects)
{
    public async Task<byte[]> ExportAsync(string compositionId, string versionId, CancellationToken ct = default)
    {
        var doc = await app.GetCompositionAsync(compositionId);
        var v = doc.Versions.FirstOrDefault(x => x.VersionId == versionId)
            ?? throw new ServiceException(404, $"version '{versionId}' not found");

        var composite = await objects.ReadAsync(v.CompositeArtifact, ct);
        var overlay = await objects.ReadAsync(v.OverlayArtifact, ct);
        var mask = await objects.ReadAsync(v.SourceMaskArtifact, ct);
        var scores = await objects.ReadAsync(v.ScoresArtifact, ct);
        var transforms = await objects.ReadAsync(v.TransformArtifact, ct);

        var manifest = new ExportManifest
        {
            ExportedAt = DateTimeOffset.UtcNow.ToString("O"),
            CompositionId = doc.Id,
            CompositionName = doc.Name,
            VersionId = v.VersionId,
            ParentVersionId = v.ParentVersionId,
            RuleSetVersion = v.RuleSetVersion,
            CanvasWidth = v.CanvasWidth,
            CanvasHeight = v.CanvasHeight,
            Frames = v.Frames.Select((f, i) => new PackageFrame
            {
                FrameIndex = i,
                EvidenceId = f.EvidenceId,
                ContentSha256 = f.ContentSha256,
                OriginalFilename = f.OriginalFilename,
                ReceivedAt = f.ReceivedAt,
                EvidenceFile = $"evidence/{i:D3}-{f.ContentSha256[..12]}.png"
            }).ToList()
        };

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteEntry(zip, "manifest.json",
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, JsonOptions.Indented)), ct);
            await WriteEntry(zip, manifest.CompositeFile, composite, ct);
            await WriteEntry(zip, manifest.SourceMaskFile, mask, ct);
            await WriteEntry(zip, manifest.OverlayFile, overlay, ct);
            await WriteEntry(zip, manifest.TransformsFile, transforms, ct);
            await WriteEntry(zip, manifest.ScoresFile, scores, ct);
            foreach (var pf in manifest.Frames)
            {
                var bytes = await objects.ReadAsync(pf.ContentSha256 + ".png", ct);
                await WriteEntry(zip, pf.EvidenceFile, bytes, ct);
            }
        }
        return ms.ToArray();
    }

    private static async Task WriteEntry(ZipArchive zip, string name, byte[] bytes, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var es = entry.Open();
        await es.WriteAsync(bytes, ct);
    }

    /// <summary>
    /// Verifies an export package without trusting it: checks embedded evidence hashes,
    /// recomputes the composition deterministically from (transforms.json + parameters + evidence),
    /// and compares the source-index mask and the composite pixel by pixel.
    /// </summary>
    public async Task<ImportReport> VerifyPackageAsync(byte[] package, CancellationToken ct = default)
    {
        using var ms = new MemoryStream(package);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

        var manifest = ReadJson<ExportManifest>(zip, "manifest.json");
        var transformDoc = ReadJson<TransformDoc>(zip, manifest.TransformsFile);
        var packagedComposite = ReadEntry(zip, manifest.CompositeFile);
        var packagedMask = ReadEntry(zip, manifest.SourceMaskFile);
        var packagedOverlay = ReadEntry(zip, manifest.OverlayFile);

        var report = new ImportReport
        {
            PackageVersionId = manifest.VersionId,
            RuleSetVersion = manifest.RuleSetVersion,
            CanvasWidth = manifest.CanvasWidth,
            CanvasHeight = manifest.CanvasHeight,
            CompositeSha256 = Sha(packagedComposite)
        };

        var frames = new List<FrameRef>();
        var images = new Dictionary<string, PngImage>();
        foreach (var pf in manifest.Frames)
        {
            var bytes = ReadEntry(zip, pf.EvidenceFile);
            string actual = Sha(bytes);
            if (!string.Equals(actual, pf.ContentSha256, StringComparison.OrdinalIgnoreCase))
            {
                report.Issues.Add(new VerificationIssue
                {
                    Code = "evidence-hash-mismatch",
                    Message = $"embedded evidence for frame {pf.FrameIndex} ({pf.OriginalFilename}) has {actual}, expected {pf.ContentSha256}"
                });
            }
            var img = Png.Decode(bytes);
            images[pf.EvidenceId] = img;
            var tf = transformDoc.Frames.First(f => f.EvidenceId == pf.EvidenceId);
            frames.Add(new FrameRef
            {
                EvidenceId = pf.EvidenceId,
                ContentSha256 = pf.ContentSha256,
                OriginalFilename = pf.OriginalFilename,
                ReceivedAt = pf.ReceivedAt,
                Magnification = tf.Magnification,
                MagnificationMissing = tf.MagnificationMissing,
                Stage = new StagePosition(tf.StageXUm, tf.StageYUm, tf.StageXUnit, tf.StageYUnit),
                ZHeightUm = tf.ZHeightUm,
                CapturedAt = tf.CapturedAt,
                Width = img.Width,
                Height = img.Height,
                Channels = img.Channels
            });
        }

        // Rebuild tiles/exclusions/forcing from scores.json so the recomputation is fully specified.
        var scoresDoc = ReadJson<ScoresDoc>(zip, manifest.ScoresFile);
        var priorTiles = scoresDoc.Tiles.Select(t => new TileRec
        {
            Index = t.TileIndex,
            X = t.X,
            Y = t.Y,
            Width = t.Width,
            Height = t.Height,
            Validated = t.Validated,
            ExcludedEvidenceIds = t.ExcludedEvidenceIds,
            ForcedEvidenceId = t.ForcedEvidenceId
        }).ToList();

        var compositor = new Compositor(transformDoc.Parameters);
        var rendered = compositor.Render(frames, images, priorTiles, overrides: null);

        var recomputedComposite = Png.Decode(rendered.Artifacts.CompositePng);
        var packagedCompositeImg = Png.Decode(packagedComposite);
        var recomputedMask = Png.Decode(rendered.Artifacts.SourceMaskPng);
        var packagedMaskImg = Png.Decode(packagedMask);
        var recomputedOverlay = Png.Decode(rendered.Artifacts.OverlayPng);
        var packagedOverlayImg = Png.Decode(packagedOverlay);

        if (recomputedComposite.Width != packagedCompositeImg.Width ||
            recomputedComposite.Height != packagedCompositeImg.Height ||
            recomputedMask.Width != packagedMaskImg.Width)
        {
            report.Issues.Add(new VerificationIssue { Code = "canvas-size-mismatch",
                Message = $"recomputed {recomputedComposite.Width}x{recomputedComposite.Height} vs packaged {packagedCompositeImg.Width}x{packagedCompositeImg.Height}" });
        }

        int w = Math.Min(recomputedComposite.Width, packagedCompositeImg.Width);
        int h = Math.Min(recomputedComposite.Height, packagedCompositeImg.Height);
        int checkedPixels = 0, mismatches = 0;
        const int maxDetailedIssues = 20;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            checkedPixels++;
            byte c = recomputedComposite.GrayAt(x, y);
            byte pc = packagedCompositeImg.GrayAt(x, y);
            byte m = recomputedMask.GrayAt(x, y);
            byte pm = packagedMaskImg.GrayAt(x, y);
            byte o = recomputedOverlay.GrayAt(x, y);
            byte po = packagedOverlayImg.GrayAt(x, y);
            if (c != pc || m != pm || o != po)
            {
                mismatches++;
                if (report.Issues.Count(i => i.Code == "pixel-mismatch") < maxDetailedIssues)
                    report.Issues.Add(new VerificationIssue
                    {
                        Code = "pixel-mismatch",
                        PixelIndex = (long)y * w + x,
                        X = x,
                        Y = y,
                        Message = $"({x},{y}) composite {c}!={pc}, sourceIndex {m}!={pm}, trust {o}!={po}"
                    });
            }
        }

        report.PixelsChecked = checkedPixels;
        report.MismatchedPixels = mismatches;
        report.RecomputedSha256 = Sha(rendered.Artifacts.CompositePng);
        if (manifest.RuleSetVersion != RuleSet.Version)
        {
            report.Issues.Add(new VerificationIssue
            {
                Code = "ruleset-version-different",
                Message = $"package produced by {manifest.RuleSetVersion}; recomputing with {RuleSet.Version}"
            });
        }
        report.Verified = report.Issues.Count == 0 && mismatches == 0;
        return report;
    }


    private static readonly JsonSerializerOptions ImportJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static T ReadJson<T>(ZipArchive zip, string name)
    {
        using var s = zip.GetEntry(name)?.Open()
            ?? throw new ServiceException(400, $"package missing '{name}'");
        using var sr = new StreamReader(s);
        return JsonSerializer.Deserialize<T>(sr.ReadToEnd(), ImportJsonOptions)
            ?? throw new ServiceException(400, $"invalid '{name}'");
    }

    private static byte[] ReadEntry(ZipArchive zip, string name)
    {
        using var s = zip.GetEntry(name)?.Open()
            ?? throw new ServiceException(400, $"package missing '{name}'");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static string Sha(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

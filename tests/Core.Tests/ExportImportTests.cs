using MicroFocus.Core.Maintenance;
using MicroFocus.Core.Services;
using MicroFocus.Core.Storage;
using Xunit;

namespace MicroFocus.Core.Tests;

public class ExportImportTests
{
    private sealed class ReadyStack : IDisposable
    {
        public ServiceHarness H { get; } = new();
        public JobRecord Job { get; }
        public VersionRecord Version { get; }
        public CompositeRecord Composite { get; }
        public ReadyStack()
        {
            Job = H.NewJob();
            H.AddFrame(Job.JobId, TestData.DetailFrame(72, 60, 4, 4, 16, 1), "a.pgm", sx: 0, sy: 0, z: 0);
            H.AddFrame(Job.JobId, TestData.DetailFrame(72, 60, 40, 30, 16, 2), "b.pgm", sx: 0, sy: 0, z: 1);
            Composite = H.Composites.CreateComposite(Job.JobId, "c");
            var p = H.Composites.Process(Composite.CompositeId);
            Version = H.Composites.Publish(Composite.CompositeId, p.Revision, "v1").Version;
        }
        public void Dispose() => H.Dispose();
    }

    [Fact]
    public void ExportPackage_ContainsComposite_Mask_Transforms_AndManifest()
    {
        using var s = new ReadyStack();
        var pkg = s.H.Exports.ExportVersion(s.Version.VersionId);
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(pkg), System.IO.Compression.ZipArchiveMode.Read);
        foreach (var name in new[] { "composite.pgm", "source-index.u16", "trust.u8", "manifest.json" })
            Assert.NotNull(zip.GetEntry(name));
    }

    [Fact]
    public void Import_RecomputesProvenance_PerPixel_Verifies()
    {
        using var s = new ReadyStack();
        var pkg = s.H.Exports.ExportVersion(s.Version.VersionId);
        var record = s.H.Exports.Import(s.Job.JobId, "pkg.zip", pkg);
        Assert.Equal(ImportStatus.Verified, record.Status);
        Assert.Equal((long)s.Version.CanvasWidth * s.Version.CanvasHeight, record.Audit.PixelsChecked);
        Assert.Equal(0, record.Audit.ProvenanceMismatches);
        Assert.Equal(0, record.Audit.TrustMismatches);
        Assert.Equal(s.Version.VersionId, record.MatchedVersionId);
    }

    [Fact]
    public void Import_DetectsTamperedSourceMask()
    {
        using var s = new ReadyStack();
        var pkg = s.H.Exports.ExportVersion(s.Version.VersionId);
        pkg = TamperZipEntry(pkg, "source-index.u16", b => { b[20] = (byte)(b[20] ^ 0xF0); b[21] = (byte)(b[21] ^ 0x0F); });
        var record = s.H.Exports.Import(s.Job.JobId, "tampered.zip", pkg);
        Assert.Equal(ImportStatus.Failed, record.Status);
        Assert.True(record.Audit.ProvenanceMismatches > 0,
            $"expected provenance mismatches, got errors: {string.Join(";", record.Audit.Errors)}");
        Assert.Contains(record.Audit.Errors, e => e.Contains("不一致"));
    }

    [Fact]
    public void Import_Fails_WhenFingerprintUnknownToJob()
    {
        using var s = new ReadyStack();
        var otherJob = s.H.Jobs.CreateJob("空任务");
        var pkg = s.H.Exports.ExportVersion(s.Version.VersionId);
        var record = s.H.Exports.Import(otherJob.JobId, "pkg.zip", pkg);
        Assert.Equal(ImportStatus.Failed, record.Status);
        Assert.Equal(2, record.Audit.MissingFrameFingerprints.Count);
    }

    [Fact]
    public void Version_KeepsOldFingerprint_WhenSameNameFileReuploaded()
    {
        using var s = new ReadyStack();
        var originalSha = s.Version.Fingerprints.Single(f => f.OriginalFileName == "a.pgm").Sha256;
        // Re-upload a different image under the same file name into the same job.
        var (newFrame, _) = s.H.Jobs.UploadFrame(new FrameUploadRequest(
            s.Job.JobId, "a.pgm", TestData.FlatFrame(72, 60, 7),
            20, false, 0.32, "um", 0, 0, 5, DateTimeOffset.UtcNow, FrameRole.Primary, null));
        Assert.NotEqual(originalSha, newFrame.Sha256);
        var pkg = s.H.Exports.ExportVersion(s.Version.VersionId);
        var record = s.H.Exports.Import(s.Job.JobId, "pkg.zip", pkg);
        Assert.Equal(ImportStatus.Verified, record.Status);
    }

    [Fact]
    public void Recovery_FlagsCorruptEvidence_AndInterruptedCandidates()
    {
        using var s = new ReadyStack();
        // corrupt evidence bytes
        var frame = s.H.Jobs.ListFrames(s.Job.JobId)[0];
        var path = s.H.Jobs.Evidence.ResolvePath(frame.Sha256);
        var raw = File.ReadAllBytes(path);
        raw[40] ^= 0xFF;
        File.WriteAllBytes(path, raw);

        var report = s.H.Recovery.Run();
        Assert.Contains(frame.Sha256, string.Join(' ', report.CorruptEvidence));
        Assert.False(report.Healthy);
    }

    private static byte[] TamperZipEntry(byte[] zipBytes, string entryName, Action<byte[]> mutate)
    {
        using var src = new System.IO.Compression.ZipArchive(new MemoryStream(zipBytes), System.IO.Compression.ZipArchiveMode.Read);
        using var outMs = new MemoryStream();
        using (var dst = new System.IO.Compression.ZipArchive(outMs, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            foreach (var entry in src.Entries)
            {
                var ne = dst.CreateEntry(entry.Name);
                using var os = ne.Open();
                using var ins = entry.Open();
                using var ms = new MemoryStream();
                ins.CopyTo(ms);
                var bytes = ms.ToArray();
                if (entry.Name == entryName) mutate(bytes);
                os.Write(bytes);
            }
        }
        return outMs.ToArray();
    }
}

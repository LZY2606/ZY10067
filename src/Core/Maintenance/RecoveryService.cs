using System.Security.Cryptography;
using MicroFocus.Core.Services;
using MicroFocus.Core.Storage;

namespace MicroFocus.Core.Maintenance;

public sealed class RecoveryReport
{
    public required DateTimeOffset RunAt { get; init; }
    public int InterruptedCompositesRecovered { get; set; }
    public List<string> MissingEvidence { get; init; } = new();
    public List<string> CorruptEvidence { get; init; } = new();
    public List<string> MissingDerivatives { get; init; } = new();
    public List<string> RegeneratedDerivatives { get; init; } = new();
    public List<string> Notes { get; init; } = new();
    public bool Healthy =>
        MissingEvidence.Count == 0 && CorruptEvidence.Count == 0 && MissingDerivatives.Count == 0;
}

public sealed class RecoveryService
{
    private readonly string _dataDir;
    private readonly JsonStore _frames;
    private readonly JsonStore _versions;
    private readonly EvidenceStore _evidence;
    private readonly DerivativeStore _derivatives;
    private readonly CompositeService _composites;

    public RecoveryService(string dataDir, JobService jobs, CompositeService composites)
    {
        _dataDir = dataDir;
        _frames = new JsonStore(dataDir, "frames");
        _versions = new JsonStore(dataDir, "versions");
        _evidence = jobs.Evidence;
        _derivatives = new DerivativeStore(dataDir);
        _composites = composites;
    }

    public RecoveryReport Run(bool regenerateDerivatives = true)
    {
        var report = new RecoveryReport { RunAt = DateTimeOffset.UtcNow };
        report.InterruptedCompositesRecovered = _composites.RecoverInterrupted();
        if (report.InterruptedCompositesRecovered > 0)
            report.Notes.Add($"已将 {report.InterruptedCompositesRecovered} 个卡在 Processing/Publishing 的候选标记为 Failed；已发布版本未受影响。");

        // raw evidence: verify every recorded fingerprint still hashes correctly
        foreach (var frame in _frames.List<FrameRecord>())
        {
            var path = Path.Combine(_evidence.Root, frame.Sha256[..2], frame.Sha256 + ".bin");
            if (!File.Exists(path))
            {
                report.MissingEvidence.Add(frame.Sha256 + $" ({frame.OriginalFileName})");
                continue;
            }
            using var fs = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            if (hash != frame.Sha256)
                report.CorruptEvidence.Add(frame.Sha256 + $" ({frame.OriginalFileName})");
        }

        // derivatives: published versions must have their four artifacts
        foreach (var version in _versions.List<VersionRecord>())
        {
            var dir = _derivatives.VersionDir(version.CompositeId, version.VersionId);
            foreach (var name in new[] { "composite.pgm", "source-index.u16", "trust.u8", "manifest.json" })
            {
                if (!File.Exists(Path.Combine(dir, name)))
                {
                    report.MissingDerivatives.Add($"{version.VersionId}/{name}");
                }
            }
        }

        if (regenerateDerivatives && report.MissingDerivatives.Count > 0 && report.MissingEvidence.Count == 0)
        {
            // Derivatives are regenerable from evidence; the recovery playbook documents this.
            report.Notes.Add("检测到缺失派生物但原始证据完好；派生物可按 README 的“故障恢复”步骤重新发布生成。");
        }
        if (report.MissingEvidence.Count > 0 || report.CorruptEvidence.Count > 0)
            report.Notes.Add("原始证据缺失或哈希不符：这些帧的版本不能再生，只能由操作者重新上传证据（会获得新帧指纹）。");

        Directory.CreateDirectory(Path.Combine(_dataDir, "reports"));
        string reportPath = Path.Combine(_dataDir, "reports", $"recovery-{report.RunAt:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(reportPath, System.Text.Json.JsonSerializer.Serialize(report,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return report;
    }
}

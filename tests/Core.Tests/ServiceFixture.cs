using MicroFocus.Core.Export;
using MicroFocus.Core.Maintenance;
using MicroFocus.Core.Services;
using MicroFocus.Core.Storage;

namespace MicroFocus.Core.Tests;

public sealed class ServiceHarness : IDisposable
{
    public string Dir { get; }
    public JobService Jobs { get; }
    public CompositeService Composites { get; }
    public ExportService Exports { get; }
    public RecoveryService Recovery { get; }

    public ServiceHarness()
    {
        Dir = Path.Combine(Path.GetTempPath(), "microfocus-tests-" + Guid.NewGuid().ToString("N"));
        Jobs = new JobService(Dir);
        Composites = new CompositeService(Dir, Jobs, defaultTileSize: 32);
        Exports = new ExportService(Dir, Jobs);
        Recovery = new RecoveryService(Dir, Jobs, Composites);
    }

    public JobRecord NewJob() => Jobs.CreateJob("测试任务");

    public FrameRecord AddFrame(string jobId, byte[] bytes, string name,
        double mag = 20, double? pixelSize = 0.32, string unit = "um",
        double? sx = 0, double? sy = 0, double? z = null, FrameRole role = FrameRole.Primary)
    {
        var (f, _) = Jobs.UploadFrame(new FrameUploadRequest(
            jobId, name, bytes, mag, false, pixelSize, unit, sx, sy, z,
            DateTimeOffset.UtcNow, role, null));
        return f;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); } catch { }
    }
}

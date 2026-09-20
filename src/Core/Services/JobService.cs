using MicroFocus.Core.Imaging;
using MicroFocus.Core.Storage;

namespace MicroFocus.Core.Services;

public sealed record FrameUploadRequest(
    string JobId,
    string OriginalFileName,
    byte[] Bytes,
    double? Magnification,
    bool MagnificationInferred,
    double? PixelSizeUm,
    string? StageUnit,
    double? StageX,
    double? StageY,
    double? Z,
    DateTimeOffset? CapturedAt,
    FrameRole Role,
    string? DustNote);

/// <summary>Accepts raw evidence. Evidence bytes are immutable; a re-upload with the same name but different bytes always gets a new frame id.</summary>
public sealed class JobService
{
    private readonly JsonStore _jobs;
    private readonly JsonStore _frames;
    private readonly EvidenceStore _evidence;
    private readonly object _gate = new();

    public JobService(string dataDir)
    {
        _jobs = new JsonStore(dataDir, "jobs");
        _frames = new JsonStore(dataDir, "frames");
        _evidence = new EvidenceStore(dataDir);
    }

    public EvidenceStore Evidence => _evidence;

    public JobRecord CreateJob(string name)
    {
        var job = new JobRecord
        {
            JobId = "job_" + Guid.NewGuid().ToString("N")[..12],
            Name = string.IsNullOrWhiteSpace(name) ? "未命名任务" : name.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        lock (_gate) _jobs.Put(job.JobId, job);
        return job;
    }

    public IReadOnlyList<JobRecord> ListJobs() => _jobs.List<JobRecord>().OrderBy(j => j.CreatedAt).ToList();
    public JobRecord GetJob(string jobId) => _jobs.Get<JobRecord>(jobId);
    public FrameRecord GetFrame(string frameId) => _frames.Get<FrameRecord>(frameId);
    public IReadOnlyList<FrameRecord> ListFrames(string jobId) =>
        _jobs.Get<JobRecord>(jobId).FrameIds.Select(_frames.Get<FrameRecord>).ToList();

    public PnmCodec.PnmImage LoadImage(FrameRecord frame) =>
        PnmCodec.Decode(_evidence.Read(frame.Sha256));

    public (FrameRecord frame, bool deduplicated) UploadFrame(FrameUploadRequest req)
    {
        _jobs.Get<JobRecord>(req.JobId);
        PnmCodec.PnmImage image;
        try
        {
            image = PnmCodec.Decode(req.Bytes);
        }
        catch (Exception ex)
        {
            throw new DomainException("BAD_IMAGE", $"无法解析图像 {req.OriginalFileName}：{ex.Message}");
        }

        bool stageTrusted = !string.IsNullOrWhiteSpace(req.StageUnit)
            && req.StageX is not null && req.StageY is not null;
        double? sx = null, sy = null;
        if (stageTrusted && (req.StageX is not null) && (req.StageY is not null))
            (sx, sy) = ConvertStage(req.StageX!.Value, req.StageY!.Value, req.StageUnit!);

        lock (_gate)
        {
            var stored = _evidence.Store(req.Bytes);

            // Deduplicate only when both the immutable bytes AND acquisition metadata are identical.
            // Same bytes uploaded as a different z-plane / position / magnification is a distinct
            // piece of evidence and must keep its own identity/fingerprint context.
            var existing = _jobs.Get<JobRecord>(req.JobId).FrameIds
                .Select(id => _frames.Get<FrameRecord>(id))
                .FirstOrDefault(f => f.Sha256 == stored.Sha256
                    && NullableEquals(f.Magnification, req.Magnification)
                    && NullableEquals(f.PixelSizeUm, req.PixelSizeUm)
                    && NullableEquals(f.StageXUm, sx)
                    && NullableEquals(f.StageYUm, sy)
                    && NullableEquals(f.ZUm, req.Z)
                    && f.StageCoordinatesTrusted == stageTrusted);
            if (existing != null)
                return (existing, true);

            var rec = new FrameRecord
            {
                FrameId = "frm_" + Guid.NewGuid().ToString("N")[..12],
                JobId = req.JobId,
                OriginalFileName = req.OriginalFileName,
                Sha256 = stored.Sha256,
                SizeBytes = stored.SizeBytes,
                Width = image.Width,
                Height = image.Height,
                Channels = image.Channels,
                MaxValue = image.MaxValue,
                Magnification = req.Magnification,
                MagnificationInferred = req.MagnificationInferred,
                PixelSizeUm = req.PixelSizeUm,
                PixelSizeInferred = false,
                StageUnit = stageTrusted ? req.StageUnit : req.StageUnit,
                StageXUm = sx,
                StageYUm = sy,
                StageCoordinatesTrusted = stageTrusted,
                ZUm = req.Z,
                CapturedAt = req.CapturedAt ?? DateTimeOffset.UtcNow,
                ReceivedAt = DateTimeOffset.UtcNow,
                Role = req.Role,
                DustNote = req.DustNote,
            };
            // True when no explicit pixel size was supplied; the planner resolves and reports it.
            rec.PixelSizeInferred = req.PixelSizeUm is null or <= 0;

            _frames.Put(rec.FrameId, rec);
            var job = _jobs.Get<JobRecord>(req.JobId);
            job.FrameIds.Add(rec.FrameId);
            _jobs.Put(job.JobId, job);
            return (rec, false);
        }
    }

    private static bool NullableEquals(double? a, double? b) =>
        a is null && b is null ? true : a is not null && b is not null && Math.Abs(a.Value - b.Value) < 1e-12;

    private static (double x, double y) ConvertStage(double x, double y, string unit)
    {
        double factor = unit.Trim().ToLowerInvariant() switch
        {
            "um" or "µm" or "micrometer" or "micrometers" => 1.0,
            "mm" => 1000.0,
            "nm" => 0.001,
            _ => throw new DomainException("BAD_STAGE_UNIT", $"不支持的载台坐标单位 '{unit}'（支持 um/mm/nm）"),
        };
        return (x * factor, y * factor);
    }
}

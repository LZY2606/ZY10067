using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MicroFocus.Core.Imaging;
using MicroFocus.Core.Services;
using MicroFocus.Core.Storage;

namespace MicroFocus.Core.Export;

public static class ExportFileNames
{
    public const string Composite = "composite.pgm";
    public const string SourceIndex = "source-index.u16";
    public const string Trust = "trust.u8";
    public const string Manifest = "manifest.json";
}

public sealed class ExportService
{
    private readonly JsonStore _versions;
    private readonly DerivativeStore _derivatives;
    private readonly JsonStore _imports;
    private readonly JobService _jobs;

    public ExportService(string dataDir, JobService jobs)
    {
        _versions = new JsonStore(dataDir, "versions");
        _derivatives = new DerivativeStore(dataDir);
        _imports = new JsonStore(dataDir, "imports");
        _jobs = jobs;
    }

    public byte[] ExportVersion(string versionId)
    {
        var version = _versions.Get<VersionRecord>(versionId);
        var dir = _derivatives.VersionDir(version.CompositeId, version.VersionId);
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, ExportFileNames.Composite, File.ReadAllBytes(Path.Combine(dir, "composite.pgm")));
            WriteEntry(zip, ExportFileNames.SourceIndex, File.ReadAllBytes(Path.Combine(dir, "source-index.u16")));
            WriteEntry(zip, ExportFileNames.Trust, File.ReadAllBytes(Path.Combine(dir, "trust.u8")));
            var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
            WriteEntry(zip, ExportFileNames.Manifest, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(version, opts)));
        }
        return ms.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var s = entry.Open();
        s.Write(bytes);
    }

    public sealed class ImportOutcome
    {
        public required ImportRecord Record { get; init; }
    }

    /// <summary>
    /// Import a package into a job. Frame references are matched by SHA-256 fingerprints,
    /// transforms are re-derived from immutable evidence, and every pixel's source index
    /// and trust code is audited against the package rasters.
    /// </summary>
    public ImportRecord Import(string jobId, string packageName, byte[] package)
    {
        var audit = new PixelAudit();
        var record = new ImportRecord
        {
            ImportId = "imp_" + Guid.NewGuid().ToString("N")[..12],
            JobId = jobId,
            CreatedAt = DateTimeOffset.UtcNow,
            PackageName = packageName,
            RuleVersion = CompositeService.FocusRuleVersion,
            Audit = audit,
        };
        try
        {
            var job = _jobs.GetJob(jobId);
            using var ms = new MemoryStream(package);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

            var manifestEntry = zip.GetEntry(ExportFileNames.Manifest)
                ?? throw new InvalidDataException("缺少 manifest.json");
            VersionRecord? manifest;
            using (var s = manifestEntry.Open())
            using (var sr = new StreamReader(s))
                manifest = JsonSerializer.Deserialize<VersionRecord>(sr.ReadToEnd(), opts)
                    ?? throw new InvalidDataException("manifest.json 无法解析");

            var compositeBytes = ReadEntry(zip, ExportFileNames.Composite);
            var sourceBytes = ReadEntry(zip, ExportFileNames.SourceIndex);
            var trustBytes = ReadEntry(zip, ExportFileNames.Trust);

            int n = manifest.CanvasWidth * manifest.CanvasHeight;
            if (sourceBytes.Length != n * 2L)
            {
                audit.Errors.Add($"source-index.u16 长度 {sourceBytes.Length} 与画布 {n} 像素不匹配");
                return Fail(record, audit);
            }
            if (trustBytes.Length != n)
            {
                audit.Errors.Add($"trust.u8 长度 {trustBytes.Length} 与画布 {n} 像素不匹配");
                return Fail(record, audit);
            }

            // 1) evidence fingerprints must match frames already received by this job
            var jobFrames = job.FrameIds.Select(_jobs.GetFrame).ToDictionary(f => f.Sha256, f => f);
            var matchedFrames = new List<Frame>();
            foreach (var fp in manifest.Fingerprints)
            {
                if (!jobFrames.TryGetValue(fp.Sha256, out var frame))
                {
                    audit.MissingFrameFingerprints.Add(fp.Sha256);
                    continue;
                }
                if (!_jobs.Evidence.Verify(fp.Sha256))
                {
                    audit.FileHashMismatches++;
                    audit.Errors.Add($"证据 {fp.Sha256} 本地哈希校验失败（证据可能被篡改或损坏）");
                    continue;
                }
                if (frame.OriginalFileName != fp.OriginalFileName)
                {
                    // same bytes, different name is allowed; record mismatch as error context is unnecessary.
                }
                matchedFrames.Add(new Frame { Record = frame, Image = _jobs.LoadImage(frame) });
            }
            if (audit.MissingFrameFingerprints.Count > 0 || audit.FileHashMismatches > 0)
            {
                audit.Errors.Add("存在缺失或被篡改的输入指纹，无法逐像素复核。");
                return Fail(record, audit);
            }
            if (matchedFrames.Count == 0)
            {
                audit.Errors.Add("包内没有任何帧指纹与任务已有证据匹配。");
                return Fail(record, audit);
            }

            // 2) re-derive geometry + transforms from raw evidence and recompute provenance
            var plan = AlignmentPlanner.Plan(matchedFrames, manifest.TileSize);
            if (plan.Width != manifest.CanvasWidth || plan.Height != manifest.CanvasHeight)
            {
                audit.Errors.Add($"重算画布 {plan.Width}x{plan.Height} 与包内 {manifest.CanvasWidth}x{manifest.CanvasHeight} 不一致");
                return Fail(record, audit);
            }

            var excluded = new HashSet<string>(manifest.ExcludedFrameIds);
            var overrides = manifest.Tiles.Where(t => t.ManualFrameId != null)
                .ToDictionary(t => t.Index, t => t.ManualFrameId!);
            var compositor = new Compositor(new CompositorOptions { TileSize = manifest.TileSize });

            var artifacts = new List<TileArtifact>();
            foreach (var r in compositor.Compose(plan, excluded, overrides))
                artifacts.Add(new TileArtifact
                {
                    Index = r.Index, X = r.X, Y = r.Y, Width = r.Width, Height = r.Height,
                    Gray = r.Gray, SourceIndex = r.SourceIndex, Trust = r.Trust,
                });
            var rasters = VersionDerivatives.Assemble(artifacts, plan.Width, plan.Height);

            var importedSource = new ushort[n];
            for (int i = 0; i < n; i++)
                importedSource[i] = (ushort)((sourceBytes[i * 2] << 8) | sourceBytes[i * 2 + 1]);

            long provMiss = 0, trustMiss = 0;
            for (int i = 0; i < n; i++)
            {
                if (importedSource[i] != rasters.SourceIndex[i]) provMiss++;
                if (trustBytes[i] != rasters.Trust[i]) trustMiss++;
            }
            audit.PixelsChecked = n;
            audit.ProvenanceMismatches = provMiss;
            audit.TrustMismatches = trustMiss;

            // 3) composite pixels must match the recomputed gray raster too
            var importedImage = PnmCodec.Decode(compositeBytes);
            if (importedImage.Width != plan.Width || importedImage.Height != plan.Height)
            {
                audit.Errors.Add("composite.pgm 尺寸与重算画布不一致");
            }
            else
            {
                long grayMiss = 0;
                for (int i = 0; i < n; i++)
                    if (importedImage.Gray[i] != rasters.Gray[i]) grayMiss++;
                if (grayMiss > 0)
                {
                    audit.ProvenanceMismatches += grayMiss;
                    audit.Errors.Add($"composite.pgm 有 {grayMiss} 个像素与按来源重算结果不一致");
                }
            }

            // 4) transforms in the manifest must be invertible and agree with the re-derived ones
            for (int i = 0; i < manifest.Transforms.Count; i++)
            {
                var declared = manifest.Transforms[i];
                var rederived = CompositeService.BuildTransforms(plan)
                    .FirstOrDefault(t => t.FrameId == declared.FrameId);
                if (rederived == null)
                {
                    audit.Errors.Add($"manifest 中的变换引用了未知帧 {declared.FrameId}");
                    continue;
                }
                for (int r = 0; r < 2; r++)
                for (int c = 0; c < 3; c++)
                    if (Math.Abs(declared.PixelToCanvas[r][c] - rederived.PixelToCanvas[r][c]) > 1e-6)
                        audit.Errors.Add($"帧 {declared.FrameId} 的变换矩阵 [{r},{c}] 与原始证据重算结果不一致");
            }

            if (provMiss > 0 && !audit.Errors.Any(e => e.Contains("来源索引")))
                audit.Errors.Add($"source-index.u16 有 {provMiss} 个像素的来源帧索引与按原始证据重算结果不一致");
            if (trustMiss > 0 && !audit.Errors.Any(e => e.Contains("可信度")))
                audit.Errors.Add($"trust.u8 有 {trustMiss} 个像素的可信度代码与重算结果不一致");

            if (audit.Errors.Count == 0)
            {
                record.Status = ImportStatus.Verified;
                record.MatchedVersionId = manifest.VersionId;
            }
            else
            {
                record.Status = ImportStatus.Failed;
            }
        }
        catch (Exception ex)
        {
            audit.Errors.Add("导入异常：" + ex.Message);
            record.Status = ImportStatus.Failed;
        }

        _imports.Put(record.ImportId, record);
        return record;
    }

    public IReadOnlyList<ImportRecord> ListImports(string jobId) =>
        _imports.List<ImportRecord>().Where(i => i.JobId == jobId).OrderBy(i => i.CreatedAt).ToList();

    private static byte[] ReadEntry(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidDataException($"缺少 {name}");
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private ImportRecord Fail(ImportRecord record, PixelAudit audit)
    {
        record.Status = ImportStatus.Failed;
        _imports.Put(record.ImportId, record);
        return record;
    }
}

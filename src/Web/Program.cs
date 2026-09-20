using MicroFocus.Core;
using MicroFocus.Core.Imaging;
using MicroFocus.Core.Export;
using MicroFocus.Core.Maintenance;
using MicroFocus.Core.Services;
using MicroFocus.Core.Storage;
using MicroFocus.Web;
using Microsoft.AspNetCore.Routing;

internal static class ApiJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = Create();
    private static System.Text.Json.JsonSerializerOptions Create()
    {
        var o = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        o.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return o;
    }
}

public static class ApiResults
{
    public static Microsoft.AspNetCore.Http.IResult Json(object? value)
    {
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, ApiJson.Options);
        return Results.File(bytes, "application/json; charset=utf-8");
    }
}

public static class AppSetup
{
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MICROFOCUS_LISTEN")))
            builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("MICROFOCUS_LISTEN"));

        string dataDir = Environment.GetEnvironmentVariable("MICROFOCUS_DATA")
            ?? Path.Combine(builder.Environment.ContentRootPath, "data");
        Directory.CreateDirectory(dataDir);

        var (jobs, composites, exports, recovery, appState, recovered) = RegisterServices(builder.Services, dataDir);

        var app = builder.Build();
        Configure(app, recovered, serveStaticFiles: true);
        return app;
    }

    public static (JobService jobs, CompositeService composites, ExportService exports,
        RecoveryService recovery, AppState state, int recovered) RegisterServices(
        IServiceCollection services, string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var jobs = new JobService(dataDir);
        var composites = new CompositeService(dataDir, jobs);
        var exports = new ExportService(dataDir, jobs);
        var recovery = new RecoveryService(dataDir, jobs, composites);
        var appState = new AppState(dataDir, jobs, composites, exports, recovery);
        int recovered = composites.RecoverInterrupted();
        services.AddSingleton(appState);
        services.AddSingleton(jobs);
        services.AddSingleton(composites);
        services.AddSingleton(exports);
        services.AddSingleton(recovery);
        return (jobs, composites, exports, recovery, appState, recovered);
    }

    public static void Configure(WebApplication app,
        int recoveredInterrupted = 0, bool serveStaticFiles = true)
    {
        app.Use(async (ctx, next) =>
        {
            try
            {
                await next();
            }
            catch (ConflictException ex)
            {
                ctx.Response.StatusCode = 409;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    error = "REVISION_CONFLICT",
                    message = ex.Message,
                    currentRevision = ex.CurrentRevision,
                });
            }
            catch (DomainException ex)
            {
                ctx.Response.StatusCode = 422;
                await ctx.Response.WriteAsJsonAsync(new { error = ex.Code, message = ex.Message });
            }
        });

        if (serveStaticFiles)
        {
            app.UseDefaultFiles();
            app.UseStaticFiles();
        }

        MapEndpoints(app, recoveredInterrupted);
    }

    public static void MapEndpoints(IEndpointRouteBuilder routes,
        int recoveredInterrupted, string? dataDirOverride = null)
    {
        var sp = routes.ServiceProvider;
        var jobs = sp.GetRequiredService<JobService>();
        var composites = sp.GetRequiredService<CompositeService>();
        var exports = sp.GetRequiredService<ExportService>();
        var recovery = sp.GetRequiredService<RecoveryService>();
        string dataDir = dataDirOverride ?? sp.GetRequiredService<AppState>().DataDir;

        var api = routes.MapGroup("/api");

        api.MapGet("/health", () => Results.Ok(new { ok = true, recoveredInterrupted, dataDir }));

        // ---------- jobs ----------
        api.MapPost("/jobs", (CreateJobRequest req) => ApiResults.Json(jobs.CreateJob(req.Name ?? "")));
        api.MapGet("/jobs", () => ApiResults.Json(jobs.ListJobs()));
        api.MapGet("/jobs/{jobId}", (string jobId) => ApiResults.Json(jobs.GetJob(jobId)));
        api.MapGet("/jobs/{jobId}/frames", (string jobId) => ApiResults.Json(jobs.ListFrames(jobId)));

        api.MapPost("/jobs/{jobId}/frames", async (HttpContext ctx, string jobId) =>
        {
            if (!ctx.Request.HasFormContentType)
                throw new DomainException("BAD_REQUEST", "需要 multipart/form-data 上传。");
            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.FirstOrDefault()
                ?? throw new DomainException("NO_FILE", "请在 file 字段中提供 PGM/PPM 图像。");

            double? num(string key) =>
                double.TryParse(form[key].FirstOrDefault(), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
            DateTimeOffset? captured = DateTimeOffset.TryParse(form["capturedAt"].FirstOrDefault(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var dt) ? dt : null;
            var role = string.Equals(form["role"].FirstOrDefault(), "reshoot", StringComparison.OrdinalIgnoreCase)
                ? FrameRole.Reshoot : FrameRole.Primary;

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var (frame, dedup) = jobs.UploadFrame(new FrameUploadRequest(
                jobId, file.FileName, ms.ToArray(),
                num("magnification"),
                bool.TryParse(form["magnificationInferred"].FirstOrDefault(), out var mi) && mi,
                num("pixelSizeUm"),
                form["stageUnit"].FirstOrDefault(),
                num("stageX"), num("stageY"),
                num("zUm"),
                captured,
                role,
                form["dustNote"].FirstOrDefault()));
            return ApiResults.Json(new { frame, deduplicated = dedup });
        });

        api.MapGet("/frames/{frameId}/raw", (string frameId) =>
        {
            var frame = jobs.GetFrame(frameId);
            var bytes = jobs.Evidence.Read(frame.Sha256);
            return Results.File(bytes,
                frame.Channels == 3 ? "image/x-portable-pixmap" : "image/x-portable-graymap");
        });

        api.MapGet("/frames/{frameId}/preview.png", (string frameId) =>
        {
            var frame = jobs.GetFrame(frameId);
            var image = jobs.LoadImage(frame);
            byte[] png = image.Rgb != null
                ? PngEncoder.Rgb(image.Rgb, image.Width, image.Height)
                : PngEncoder.Grayscale(image.Gray, image.Width, image.Height);
            return Results.File(png, "image/png");
        });

        // ---------- composites ----------
        api.MapGet("/jobs/{jobId}/composites", (string jobId) => ApiResults.Json(composites.ListComposites(jobId)));
        api.MapPost("/jobs/{jobId}/composites", (string jobId, CreateCompositeRequest req) =>
            ApiResults.Json(composites.CreateComposite(jobId, req.Name ?? "")));

        api.MapGet("/composites/{id}", (string id) => ApiResults.Json(composites.GetComposite(id)));

        api.MapPost("/composites/{id}/process", (string id) => ApiResults.Json(composites.Process(id)));
        api.MapPost("/composites/{id}/reprocess", (string id) => ApiResults.Json(composites.ReprocessDirty(id)));
        api.MapPost("/composites/{id}/retry", (string id) => ApiResults.Json(composites.Retry(id)));

        api.MapPost("/composites/{id}/cancel", (string id, RevisionRequest req) =>
        {
            composites.Cancel(id, req.Revision);
            return ApiResults.Json(composites.GetComposite(id));
        });

        api.MapPost("/composites/{id}/frames/{frameId}/exclusion",
            (string id, string frameId, ExclusionRequest req) =>
                ApiResults.Json(composites.SetFrameExclusion(id, req.Revision, frameId, req.Excluded).Composite));

        api.MapPost("/composites/{id}/tiles/{tileIndex:int}/source",
            (string id, int tileIndex, TileSourceRequest req) =>
                ApiResults.Json(composites.SetTileSource(id, req.Revision, tileIndex, req.FrameId).Composite));

        api.MapPost("/composites/{id}/publish", (string id, PublishRequest req) =>
        {
            var view = composites.Publish(id, req.Revision, req.Label ?? "");
            return ApiResults.Json(new { composite = view.Composite, version = view.Version });
        });

        api.MapPost("/composites/{id}/republish", (string id, RepublishRequest req) =>
        {
            var view = composites.RepublishFromVersion(id, req.Revision, req.VersionId, req.Label ?? "");
            return ApiResults.Json(new { composite = view.Composite, version = view.Version });
        });

        api.MapPost("/composites/{id}/draft-from-version", (string id, RepublishRequest req) =>
            ApiResults.Json(composites.DraftFromVersion(id, req.Revision, req.VersionId)));

        api.MapGet("/composites/{id}/versions", (string id) => ApiResults.Json(composites.ListVersions(id)));
        api.MapGet("/versions/{versionId}", (string versionId) => ApiResults.Json(composites.GetVersion(versionId)));

        api.MapGet("/composites/{id}/tiles/{tileIndex:int}/preview.png",
            (string id, int tileIndex, string? overlay) =>
            {
                var comp = composites.GetComposite(id);
                if (tileIndex < 0 || tileIndex >= comp.Tiles.Count)
                    throw new DomainException("TILE_NOT_FOUND", $"瓦片 {tileIndex} 不存在。");
                var frames = comp.FrameIds.Select(fid =>
                {
                    var fr = jobs.GetFrame(fid);
                    return new Frame { Record = fr, Image = jobs.LoadImage(fr) };
                }).ToList();
                var plan = composites.LoadPlan(comp, frames);
                var compositor = new Compositor(new CompositorOptions { TileSize = comp.TileSize });
                var overrides = comp.Tiles.Where(t => t.ManualFrameId != null)
                    .ToDictionary(t => t.Index, t => t.ManualFrameId!);
                var result = compositor.Compose(plan, comp.ExcludedFrameIds, overrides, tileIndex).Single();

                byte[] png = overlay switch
                {
                    "trust" => PngEncoder.Rgb(TrustOverlay.Render(result.Gray, result.Trust, result.Width, result.Height), result.Width, result.Height),
                    "source" => PngEncoder.Rgb(SourceOverlay.Render(result.Gray, result.SourceIndex, result.Width, result.Height), result.Width, result.Height),
                    _ => PngEncoder.Grayscale(result.Gray, result.Width, result.Height),
                };
                return Results.File(png, "image/png");
            });

        api.MapGet("/versions/{versionId}/preview.png", (string versionId, string? overlay) =>
        {
            var version = composites.GetVersion(versionId);
            var store = new DerivativeStore(dataDir);
            var dir = store.VersionDir(version.CompositeId, version.VersionId);
            var image = PnmCodec.Decode(File.ReadAllBytes(Path.Combine(dir, "composite.pgm")));
            var trust = File.ReadAllBytes(Path.Combine(dir, "trust.u8"));
            var source = File.ReadAllBytes(Path.Combine(dir, "source-index.u16"));
            var src16 = new ushort[image.Width * (long)image.Height];
            for (long i = 0; i < src16.Length; i++)
                src16[i] = (ushort)((source[i * 2] << 8) | source[i * 2 + 1]);

            byte[] png = overlay switch
            {
                "trust" => PngEncoder.Rgb(TrustOverlay.Render(image.Gray, trust, image.Width, image.Height), image.Width, image.Height),
                "source" => PngEncoder.Rgb(SourceOverlay.Render(image.Gray, src16, image.Width, image.Height), image.Width, image.Height),
                _ => PngEncoder.Grayscale(image.Gray, image.Width, image.Height),
            };
            return Results.File(png, "image/png");
        });

        api.MapGet("/versions/{versionId}/export", (string versionId) =>
        {
            var bytes = exports.ExportVersion(versionId);
            return Results.File(bytes, "application/zip", $"export-{versionId}.zip");
        });

        api.MapPost("/jobs/{jobId}/imports", async (HttpContext ctx, string jobId) =>
        {
            if (!ctx.Request.HasFormContentType)
                throw new DomainException("BAD_REQUEST", "需要 multipart/form-data 上传导出包 zip。");
            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.FirstOrDefault()
                ?? throw new DomainException("NO_FILE", "请在 file 字段中提供导出包 zip。");
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var record = exports.Import(jobId, file.FileName, ms.ToArray());
            return ApiResults.Json(record);
        });

        api.MapGet("/jobs/{jobId}/imports", (string jobId) => ApiResults.Json(exports.ListImports(jobId)));
        api.MapPost("/admin/recovery", () => ApiResults.Json(recovery.Run()));

        // Only unknown /api paths produce JSON 404; all other non-file paths fall through
        // to the static default document (index.html) handled by the preceding middleware.
        routes.MapFallback(async ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsJsonAsync(new { error = "NOT_FOUND" });
                return;
            }
            // SPA: serve index.html for unknown non-file routes.
            var env = ctx.RequestServices.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
            var index = Path.Combine(env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot"), "index.html");
            if (File.Exists(index))
            {
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.SendFileAsync(index);
            }
            else
            {
                ctx.Response.StatusCode = 404;
            }
        });
    }

}

public static class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls("http://127.0.0.1:5203");
        var app = AppSetup.Build(builder);
        app.Run();
    }
}

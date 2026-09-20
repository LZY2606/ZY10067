using System.Text.Json;
using MicroStack.Core.Engine;
using MicroStack.Core.Exchange;
using MicroStack.Core.Models;
using MicroStack.Core.Services;
using MicroStack.Core.Storage;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

string dataDir = Environment.GetEnvironmentVariable("MICROSTACK_DATA")
    ?? Path.Combine(builder.Environment.ContentRootPath, "microstack-data");
var store = new LocalObjectStore(Path.Combine(dataDir, "objects"));
var appService = new AppService(dataDir, store);
var packageService = new PackageService(appService, store);

builder.Services.AddSingleton(appService);
builder.Services.AddSingleton(packageService);

var app = builder.Build();

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (ServiceException ex)
    {
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = ex.StatusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        var payload = ex.Payload ?? new { error = ex.Message };
        await context.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/health", () => Results.Ok(new { ok = true, ruleSet = RuleSet.Version }));

// ---------------- Evidence ----------------

api.MapPost("/evidence", async (HttpRequest request, AppService svc) =>
{
    if (!request.HasFormContentType) return Results.BadRequest(new { error = "expected multipart/form-data with 'file'" });
    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file is null) return Results.BadRequest(new { error = "missing form field 'file'" });
    using var ms = new MemoryStream();
    await file.CopyToAsync(ms);
    var result = await svc.IngestEvidenceAsync(file.FileName, ms.ToArray());
    return Results.Ok(result);
});

api.MapGet("/evidence", (AppService svc) => Results.Ok(svc.ListEvidence()));

api.MapGet("/evidence/{evidenceId}/raw", async (string evidenceId, AppService svc) =>
{
    var (meta, bytes) = await svc.LoadEvidenceAsync(evidenceId);
    return Results.File(bytes, "image/png", meta.OriginalFilename);
});

// ---------------- Demo data ----------------

api.MapPost("/demo", async (AppService svc) =>
{
    var frames = new List<FrameInput>();
    foreach (var d in DemoData.Build())
    {
        var ingested = await svc.IngestEvidenceAsync(d.Filename, d.Png);
        frames.Add(new FrameInput
        {
            EvidenceId = ingested.EvidenceId,
            Magnification = d.Magnification,
            StageX = d.StageX,
            StageY = d.StageY,
            StageXUnit = d.StageUnit,
            StageYUnit = d.StageUnit,
            ZHeightUm = d.ZUm,
            CapturedAt = d.CapturedAt
        });
    }
    var doc = await svc.CreateCompositionAsync(new CreateCompositionRequest
    {
        Name = "Demo z-stack + partial reshoot",
        Frames = frames,
        Note = "synthetic demo: 3 focal planes, 1 partial re-shot, one dusty frame"
    });
    return Results.Ok(svc.ToDetail(doc));
});

// ---------------- Compositions ----------------

api.MapGet("/compositions", async (AppService svc) =>
{
    var docs = await svc.ListCompositionsAsync();
    return Results.Ok(docs.Select(d => svc.ToDetail(d).Summary));
});

api.MapPost("/compositions", async (CreateCompositionRequest request, AppService svc) =>
{
    var doc = await svc.CreateCompositionAsync(request);
    return Results.Ok(svc.ToDetail(doc));
});

api.MapGet("/compositions/{id}", async (string id, AppService svc) =>
{
    var doc = await svc.GetCompositionAsync(id);
    return Results.Ok(svc.ToDetail(doc));
});

api.MapGet("/compositions/{id}/versions/{versionId}/artifacts/{kind}",
    async (string id, string versionId, string kind, AppService svc) =>
{
    var bytes = await svc.GetArtifactAsync(id, versionId, kind);
    string contentType = kind is "scores" or "transforms" ? "application/json" : "image/png";
    string downloadName = $"{kind}-{versionId}.{(contentType == "image/png" ? "png" : "json")}";
    return Results.File(bytes, contentType, downloadName);
});

api.MapPost("/compositions/{id}/versions", async (
    string id, CreateVersionRequest request, AppService svc) =>
{
    try
    {
        var outcome = await svc.CreateVersionAsync(id, request);
        var doc = await svc.GetCompositionAsync(id);
        var v = svc.ToVersionResult(doc, outcome.Version);
        v.AutoMerged = outcome.AutoMerged;
        return Results.Ok(v);
    }
    catch (ServiceException ex) when (ex.StatusCode == 409)
    {
        return Results.Conflict(ex.Payload ?? new { error = ex.Message });
    }
});

api.MapPost("/compositions/{id}/versions/{versionId}/tiles/validate", async (
    string id, string versionId, TileValidationRequest request, AppService svc) =>
{
    var v = await svc.SetTileValidationAsync(id, versionId, request);
    var doc = await svc.GetCompositionAsync(id);
    return Results.Ok(svc.ToVersionResult(doc, v));
});

api.MapDelete("/compositions/{id}/versions/{versionId}", async (
    string id, string versionId, AppService svc) =>
{
    await svc.CancelVersionAsync(id, versionId);
    return Results.Ok(new { canceled = versionId });
});

api.MapPost("/compositions/{id}/publish", async (
    string id, PublishRequest request, AppService svc) =>
{
    try
    {
        var result = await svc.PublishAsync(id, request);
        return Results.Ok(result);
    }
    catch (ServiceException ex) when (ex.StatusCode == 409 || ex.StatusCode == 400)
    {
        return Results.Json(ex.Payload ?? new { error = ex.Message },
            statusCode: ex.StatusCode);
    }
});

api.MapGet("/compositions/{id}/export/{versionId}", async (
    string id, string versionId, PackageService packages) =>
{
    var bytes = await packages.ExportAsync(id, versionId);
    return Results.File(bytes, "application/zip", $"microstack-{id}-{versionId}.zip");
});

api.MapPost("/imports/verify", async (HttpRequest request, PackageService packages) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "expected multipart/form-data with 'package'" });
    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("package");
    if (file is null) return Results.BadRequest(new { error = "missing form field 'package'" });
    using var ms = new MemoryStream();
    await file.CopyToAsync(ms);
    try
    {
        var report = await packages.VerifyPackageAsync(ms.ToArray());
        return Results.Ok(report);
    }
    catch (ServiceException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidDataException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

await app.RunAsync();
    }
}

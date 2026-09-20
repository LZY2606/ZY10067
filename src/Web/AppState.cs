using MicroFocus.Core.Export;
using MicroFocus.Core.Maintenance;
using MicroFocus.Core.Services;

namespace MicroFocus.Web;

public sealed class AppState
{
    public string DataDir { get; }
    public JobService Jobs { get; }
    public CompositeService Composites { get; }
    public ExportService Exports { get; }
    public RecoveryService Recovery { get; }

    public AppState(string dataDir, JobService jobs, CompositeService composites,
        ExportService exports, RecoveryService recovery)
    {
        DataDir = dataDir;
        Jobs = jobs;
        Composites = composites;
        Exports = exports;
        Recovery = recovery;
    }
}

public static class Responses
{
    public static IResult Domain(Core.Storage.DomainException ex) =>
        Results.Json(new { error = ex.Code, message = ex.Message }, statusCode: 422);

    public static IResult NotFoundTile(int index) =>
        Results.Json(new { error = "TILE_NOT_FOUND", message = $"瓦片 {index} 不存在" }, statusCode: 404);
}

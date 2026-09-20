using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace MicroFocus.Core.Tests;

public class WebApiTests : IDisposable
{
    private readonly string _dir;
    private readonly EndpointTestHost _host;

    public WebApiTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "microfocus-web-" + Guid.NewGuid().ToString("N"));
        _host = new EndpointTestHost(_dir);
    }

    private static IHeaderDictionary Multipart(string boundary, byte[] body)
    {
        var h = new HeaderDictionary { ["Content-Type"] = "multipart/form-data; boundary=" + boundary };
        return h;
    }

    public static (byte[] body, string boundary) FrameUploadPublic(string fileName, byte[] pgm,
        Dictionary<string, string> fields)
    {
        string boundary = "----mf" + Guid.NewGuid().ToString("N");
        using var ms = new MemoryStream();
        void WriteText(string s) { var b = Encoding.UTF8.GetBytes(s); ms.Write(b); }
        foreach (var (k, v) in fields)
        {
            WriteText($"--{boundary}\r\nContent-Disposition: form-data; name=\"{k}\"\r\n\r\n{v}\r\n");
        }
        WriteText($"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{fileName}\"\r\n");
        WriteText("Content-Type: application/octet-stream\r\n\r\n");
        ms.Write(pgm);
        WriteText($"\r\n--{boundary}--\r\n");
        var bytes = ms.ToArray();
        return (bytes, boundary);
    }

    private static JsonElement Json(EndpointTestHost.Response r)
    {
        if (r.StatusCode is not (200 or 201))
            throw new Xunit.Sdk.XunitException(
                $"expected success but got {r.StatusCode}: {Encoding.UTF8.GetString(r.Body)}");
        return JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(r.Body));
    }

    private static (byte[] body, string boundary) FrameUpload(string f, byte[] p,
        Dictionary<string, string> d) => FrameUploadPublic(f, p, d);

    [Fact]
    public async Task Health_Works()
    {
        var r = await _host.SendAsync("GET", "/api/health");
        Assert.Equal(200, r.StatusCode);
        var el = Json(r);
        Assert.True(el.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task EndToEnd_Upload_Process_Publish_Export_Import_ViaHttp()
    {
        var job = Json(await _host.SendAsync("POST", "/api/jobs",
            new HeaderDictionary { ["Content-Type"] = "application/json" },
            Encoding.UTF8.GetBytes("""{"name":"http"}""")));
        string jobId = job.GetProperty("jobId").GetString()!;

        var fields = (string z) => new Dictionary<string, string>
        {
            ["magnification"] = "20", ["pixelSizeUm"] = "0.32", ["stageUnit"] = "um",
            ["stageX"] = "0", ["stageY"] = "0", ["zUm"] = z,
        };
        var (bodyA, ba) = FrameUpload("a.pgm", TestData.DetailFrame(64, 64, 4, 4, 18, 1), fields("0"));
        var (bodyB, bb) = FrameUpload("b.pgm", TestData.DetailFrame(64, 64, 40, 40, 18, 2), fields("2"));
        Assert.Equal(200, (await _host.SendAsync("POST", $"/api/jobs/{jobId}/frames", Multipart(ba, bodyA), bodyA)).StatusCode);
        Assert.Equal(200, (await _host.SendAsync("POST", $"/api/jobs/{jobId}/frames", Multipart(bb, bodyB), bodyB)).StatusCode);

        var cmp = Json(await _host.SendAsync("POST", $"/api/jobs/{jobId}/composites",
            new HeaderDictionary { ["Content-Type"] = "application/json" },
            Encoding.UTF8.GetBytes("""{"name":"c"}""")));
        string cmpId = cmp.GetProperty("compositeId").GetString()!;

        var processed = Json(await _host.SendAsync("POST", $"/api/composites/{cmpId}/process"));
        Assert.Equal("Ready", processed.GetProperty("state").GetString());
        Assert.True(processed.GetProperty("tiles").GetArrayLength() > 0);

        long rev = processed.GetProperty("revision").GetInt64();
        var pub = Json(await _host.SendAsync("POST", $"/api/composites/{cmpId}/publish",
            new HeaderDictionary { ["Content-Type"] = "application/json" },
            Encoding.UTF8.GetBytes($"{{\"revision\":{rev},\"label\":\"http-v1\"}}")));
        string versionId = pub.GetProperty("version").GetProperty("versionId").GetString()!;

        var zip = await _host.SendAsync("GET", $"/api/versions/{versionId}/export");
        Assert.Equal(200, zip.StatusCode);
        Assert.True(zip.Body.Length > 100);

        var (impBody, ib) = FrameUpload("export.zip", zip.Body, new Dictionary<string, string>());
        var imp = Json(await _host.SendAsync("POST", $"/api/jobs/{jobId}/imports", Multipart(ib, impBody), impBody));
        Assert.Equal("Verified", imp.GetProperty("status").GetString());
        Assert.True(imp.GetProperty("audit").GetProperty("pixelsChecked").GetInt64() > 0);

        Assert.Equal(200, (await _host.SendAsync("POST", "/api/admin/recovery")).StatusCode);
    }

    [Fact]
    public async Task StaleRevision_Returns409_WithCurrentRevision()
    {
        var job = Json(await _host.SendAsync("POST", "/api/jobs",
            new HeaderDictionary { ["Content-Type"] = "application/json" },
            Encoding.UTF8.GetBytes("""{"name":"conflict"}""")));
        string jobId = job.GetProperty("jobId").GetString()!;

        var (b1, bn1) = FrameUpload("a.pgm", TestData.DetailFrame(64, 64, 10, 10, 12), new()
        {
            ["magnification"] = "20", ["pixelSizeUm"] = "0.32", ["stageUnit"] = "um", ["stageX"] = "0", ["stageY"] = "0",
        });
        await _host.SendAsync("POST", $"/api/jobs/{jobId}/frames", Multipart(bn1, b1), b1);
        var (b2, bn2) = FrameUpload("b.pgm", TestData.DetailFrame(64, 64, 40, 40, 12, 3), new()
        {
            ["magnification"] = "20", ["pixelSizeUm"] = "0.32", ["stageUnit"] = "um", ["stageX"] = "0", ["stageY"] = "0", ["zUm"] = "1",
        });
        await _host.SendAsync("POST", $"/api/jobs/{jobId}/frames", Multipart(bn2, b2), b2);

        var cmp = Json(await _host.SendAsync("POST", $"/api/jobs/{jobId}/composites",
            new HeaderDictionary { ["Content-Type"] = "application/json" },
            Encoding.UTF8.GetBytes("""{"name":"c"}""")));
        string cmpId = cmp.GetProperty("compositeId").GetString()!;
        var processed = Json(await _host.SendAsync("POST", $"/api/composites/{cmpId}/process"));
        long rev = processed.GetProperty("revision").GetInt64();
        string frameId = processed.GetProperty("frameIds")[0].GetString()!;

        var headers = new HeaderDictionary { ["Content-Type"] = "application/json" };
        await _host.SendAsync("POST", $"/api/composites/{cmpId}/frames/{frameId}/exclusion",
            headers, Encoding.UTF8.GetBytes($"{{\"revision\":{rev},\"excluded\":true}}"));
        var stale = await _host.SendAsync("POST", $"/api/composites/{cmpId}/frames/{frameId}/exclusion",
            headers, Encoding.UTF8.GetBytes($"{{\"revision\":{rev},\"excluded\":false}}"));
        Assert.Equal(409, stale.StatusCode);
        var el = JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(stale.Body));
        Assert.Equal("REVISION_CONFLICT", el.GetProperty("error").GetString());
        Assert.True(el.GetProperty("currentRevision").GetInt64() > rev);
    }

    [Fact]
    public async Task BadImage_Returns422_WithReadableDiagnostic()
    {
        var job = Json(await _host.SendAsync("POST", "/api/jobs",
            new HeaderDictionary { ["Content-Type"] = "application/json" },
            Encoding.UTF8.GetBytes("""{"name":"bad"}""")));
        string jobId = job.GetProperty("jobId").GetString()!;
        var fields = new Dictionary<string, string>
        {
            ["magnification"] = "20", ["stageUnit"] = "um", ["stageX"] = "0", ["stageY"] = "0",
        };
        var (body, boundary) = FrameUpload("broken.pgm", new byte[] { 1, 2, 3, 4 }, fields);
        var r = await _host.SendAsync("POST", $"/api/jobs/{jobId}/frames", Multipart(boundary, body), body);
        if (r.StatusCode != 422)
            Assert.Fail($"status={r.StatusCode} body={Encoding.UTF8.GetString(r.Body)}");
    }

    public void Dispose() => _host.Dispose();
}

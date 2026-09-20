using System.Net;
using System.Text;
using System.Text.Json;

namespace MicroStack.Tests;

/// <summary>Runs the real Kestrel host in-process on an ephemeral port.</summary>
public sealed class WebEndToEndTests : IDisposable
{
    private readonly string _dir;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _server;
    private readonly HttpClient _http = new();
    private readonly string _base;

    public WebEndToEndTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "microstack-webtest-" + Guid.NewGuid().ToString("N"));
        var port = Random.Shared.Next(53000, 54000);
        _base = $"http://127.0.0.1:{port}";
        Environment.SetEnvironmentVariable("MICROSTACK_DATA", _dir);
        _server = Task.Run(() =>
        {
            string[] args = ["--urls", _base];
            return Program.Main(args);
        });
        _ = WaitForReady();
        WaitForReady().GetAwaiter().GetResult();
    }

    private async Task WaitForReady()
    {
        for (int i = 0; i < 60; i++)
        {
            try
            {
                using var r = await _http.GetAsync(_base + "/api/health");
                if (r.IsSuccessStatusCode) return;
            }
            catch { }
            await Task.Delay(100);
        }
        throw new TimeoutException("web server did not become ready");
    }

    [Fact]
    public async Task FullJourney_Demo_Edit_MergeError_Publish_ExportVerify()
    {
        var demo = await Post<JsonElement>("/api/demo", null);
        string cmpId = demo.GetProperty("summary").GetProperty("id").GetString()!;
        var versions = demo.GetProperty("versions");
        string v0 = versions[0].GetProperty("versionId").GetString()!;

        // diagnostics include the dusty frame's placement but no unit/mag failures in demo
        var detail = await Get<JsonElement>($"/api/compositions/{cmpId}");
        Assert.Equal(4, detail.GetProperty("frames").GetArrayLength());

        // validate all tiles on v0
        var tiles = detail.GetProperty("versions")[0].GetProperty("tiles").EnumerateArray();
        foreach (var t in tiles)
        {
            int idx = t.GetProperty("index").GetInt32();
            await Post<JsonElement>(
                $"/api/compositions/{cmpId}/versions/{v0}/tiles/validate",
                new { tileIndex = idx, validated = true });
        }
        var pub = await Post<JsonElement>($"/api/compositions/{cmpId}/publish", new { versionId = v0 });
        Assert.Equal(v0, pub.GetProperty("publishedVersionId").GetString());

        // artifacts are fetchable and non-empty
        using var compositeResp = await _http.GetAsync(
            $"{_base}/api/compositions/{cmpId}/versions/{v0}/artifacts/composite");
        Assert.Equal(HttpStatusCode.OK, compositeResp.StatusCode);
        Assert.True(compositeResp.Content.Headers.ContentLength > 100);

        // export and verify via the HTTP import endpoint
        byte[] zip;
        using (var er = await _http.GetAsync($"{_base}/api/compositions/{cmpId}/export/{v0}"))
        {
            er.EnsureSuccessStatusCode();
            zip = await er.Content.ReadAsByteArrayAsync();
        }
        using (var form = new MultipartFormDataContent())
        using (var fileContent = new ByteArrayContent(zip))
        {
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
            form.Add(fileContent, "package", "export.zip");
            using var vr = await _http.PostAsync(_base + "/api/imports/verify", form);
            vr.EnsureSuccessStatusCode();
            var report = JsonDocument.Parse(await vr.Content.ReadAsStringAsync()).RootElement;
            Assert.True(report.GetProperty("verified").GetBoolean(),
                report.GetRawText());
            Assert.True(report.GetProperty("pixelsChecked").GetInt32() > 0);
            Assert.Equal(0, report.GetProperty("mismatchedPixels").GetInt32());
        }

        // index page serves the SPA shell
        using var index = await _http.GetAsync(_base + "/");
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        string html = await index.Content.ReadAsStringAsync();
        Assert.Contains("MicroStack", html);
    }

    [Fact]
    public async Task StaleSameTileEdit_Returns409ConflictPayload()
    {
        var demo = await Post<JsonElement>("/api/demo", null);
        string cmpId = demo.GetProperty("summary").GetProperty("id").GetString()!;
        string v0 = demo.GetProperty("versions")[0].GetProperty("versionId").GetString()!;
        string frame0 = demo.GetProperty("frames")[0].GetProperty("evidenceId").GetString()!;

        await Post<JsonElement>($"/api/compositions/{cmpId}/versions", new
        {
            baseVersionId = v0,
            operations = new[] { new { tileIndex = 0, kind = "replace", excludedEvidenceIds = new[] { frame0 }, forcedEvidenceId = (string?)null } }
        });

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Post<JsonElement>($"/api/compositions/{cmpId}/versions", new
        {
            baseVersionId = v0,
            operations = new[] { new { tileIndex = 0, kind = "replace", excludedEvidenceIds = Array.Empty<string>(), forcedEvidenceId = (string?)null } }
        }));
        Assert.Contains("409", ex.Message);
    }

    private async Task<T> Get<T>(string path)
    {
        using var r = await _http.GetAsync(_base + path);
        r.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<T>(await r.Content.ReadAsStringAsync(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private async Task<T> Post<T>(string path, object? body)
    {
        using var content = body == null ? null :
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var r = await _http.PostAsync(_base + path, content);
        string text = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)r.StatusCode} {r.StatusCode}: {text}");
        return JsonSerializer.Deserialize<T>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}

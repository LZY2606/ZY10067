using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using MicroFocus.Core.Storage;
using MicroFocus.Web;

namespace MicroFocus.Core.Tests;

internal sealed class InProcessRequestFeature : IHttpRequestFeature
{
    private readonly long? _contentLength;
    public InProcessRequestFeature(string method, string target, IHeaderDictionary headers, byte[]? body)
    {
        _contentLength = body?.Length;
        Method = method;
        int q = target.IndexOf('?');
        Path = q < 0 ? target : target[..q];
        QueryString = q < 0 ? "" : target[q..];
        RawTarget = target;
        Headers = headers;
        Body = new MemoryStream(body ?? Array.Empty<byte>());
    }
    public string Protocol { get; set; } = "HTTP/1.1";
    public string Scheme { get; set; } = "http";
    public string Method { get; set; }
    public string PathBase { get; set; } = "";
    public string Path { get; set; }
    public string QueryString { get; set; }
    public string RawTarget { get; set; }
    public IHeaderDictionary Headers { get; set; }
    public Stream Body { get; set; }
    public long? ContentLength { get => _contentLength; set { } }
}

internal sealed class InProcessResponseFeature : IHttpResponseFeature
{
    public int StatusCode { get; set; } = 200;
    public string? ReasonPhrase { get; set; }
    public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
    public Stream Body { get; set; } = Stream.Null;
    public bool HasStarted => false;
    public void OnStarting(Func<object, Task> callback, object state) { }
    public void OnCompleted(Func<object, Task> callback, object state) { }
}

internal sealed class InProcessResponseBodyFeature : IHttpResponseBodyFeature
{
    private readonly MemoryStream _stream = new();
    public byte[] Bytes => _stream.ToArray();
    public Stream Stream => _stream;
    public System.IO.Pipelines.PipeWriter Writer => System.IO.Pipelines.PipeWriter.Create(_stream);
    public Task CompleteAsync() => Task.CompletedTask;
    public void DisableBuffering() { }
    public Task SendFileAsync(string path, long offset, long? count, CancellationToken token) => throw new NotSupportedException();
    public Task StartAsync(CancellationToken token = default) => Task.CompletedTask;
}

internal sealed class MutableEndpointDataSource : EndpointDataSource
{
    private readonly List<Endpoint> _endpoints = new();
    public override IReadOnlyList<Endpoint> Endpoints => _endpoints;
    public void Add(Endpoint ep) => _endpoints.Add(ep);
    public override IChangeToken GetChangeToken() => new CancellationChangeToken(CancellationToken.None);
}

internal sealed class CollectingRouteBuilder : IEndpointRouteBuilder
{
    public MutableEndpointDataSource Source { get; }

    public CollectingRouteBuilder(IServiceProvider sp)
    {
        ServiceProvider = sp;
        Source = new MutableEndpointDataSource();
        DataSources.Add(Source);
    }

    public IServiceProvider ServiceProvider { get; }
    public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();

    public IApplicationBuilder CreateApplicationBuilder() =>
        throw new NotSupportedException("测试路由构建器只收集端点");
}

internal sealed class LazyFormFeature : IFormFeature
{
    private readonly HttpContext _ctx;
    private readonly Microsoft.AspNetCore.Http.Features.FormOptions _options;
    private IFormCollection? _form;
    public LazyFormFeature(HttpContext ctx, Microsoft.AspNetCore.Http.Features.FormOptions options)
    {
        _ctx = ctx;
        _options = options;
    }
    public bool HasFormContentType =>
        (_ctx.Request.ContentType ?? "").StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase)
        || (_ctx.Request.ContentType ?? "").StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
    public IFormCollection? Form { get => _form; set => _form = value; }
    public IFormCollection ReadForm() => _form ??= FormReaderFactory(_ctx, _options);
    public Task<IFormCollection> ReadFormAsync(CancellationToken ct = default) =>
        Task.FromResult(_form ??= FormReaderFactory(_ctx, _options));
    private static IFormCollection FormReaderFactory(HttpContext ctx, Microsoft.AspNetCore.Http.Features.FormOptions options)
    {
        // Rewind the shared body so each reader sees the full payload.
        if (ctx.Request.Body.CanSeek) ctx.Request.Body.Position = 0;
        return new FormFeature(ctx.Request, options).ReadForm();
    }
}

/// <summary>Offline HTTP test host: maps minimal API endpoints into an in-memory data source and routes manually.</summary>
public sealed class EndpointTestHost : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly List<RouteEndpoint> _endpoints = new();

    public EndpointTestHost(string dataDir)
    {
        Environment.SetEnvironmentVariable("MICROFOCUS_DATA", dataDir);
        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        services.AddRouting();
        services.AddHttpContextAccessor();
        services.AddOptions();
        services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(_ => { });
        services.AddLogging(b => b.ClearProviders());
        var tuple = AppSetup.RegisterServices(services, dataDir);
        _services = services.BuildServiceProvider();

        var erb = new CollectingRouteBuilder(_services);
        AppSetup.MapEndpoints(erb, tuple.recovered, dataDir);

        foreach (var ds in erb.DataSources)
            foreach (var ep in ds.Endpoints.OfType<RouteEndpoint>())
                _endpoints.Add(ep);
    }

    public void Dispose() => _services.Dispose();

    public sealed class Response
    {
        public required int StatusCode { get; init; }
        public required IHeaderDictionary Headers { get; init; }
        public required byte[] Body { get; init; }
    }

    public async Task<Response> SendAsync(string method, string target,
        IHeaderDictionary? headers = null, byte[]? body = null)
    {
        var features = new FeatureCollection();
        var hdrs = headers ?? new HeaderDictionary();
        if (body != null && hdrs.ContentLength == null) hdrs.ContentLength = body.Length;
        var requestFeature = new InProcessRequestFeature(method, target, hdrs, body);
        var pipeFeature = new BodyPipeFeature(requestFeature.Body);
        requestFeature.Body = pipeFeature.Reader.AsStream();
        features.Set<IHttpRequestFeature>(requestFeature);
        features.Set<IRequestBodyPipeFeature>(pipeFeature);
        features.Set<IHttpBodyControlFeature>(new SyncIOControl { AllowSynchronousIO = true });
        features.Set<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>(new MaxBody());
        features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseTrailersFeature>(new TestTrailers());
        features.Set<Microsoft.AspNetCore.Http.Features.IHttpResetFeature>(new TestReset());
        var resp = new InProcessResponseFeature();
        var respBody = new InProcessResponseBodyFeature();
        features.Set<IHttpResponseFeature>(resp);
        features.Set<IHttpResponseBodyFeature>(respBody);
        resp.Body = respBody.Stream;
        var ctx = new DefaultHttpContext(features) { RequestServices = _services };
        var formOptions = _services.GetService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Features.FormOptions>>()
            ?.Value ?? new Microsoft.AspNetCore.Http.Features.FormOptions();
        features.Set<IFormFeature>(new LazyFormFeature(ctx, formOptions));
        features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new BodyDetection());

        string path = ctx.Request.Path.Value ?? "/";
        RouteEndpoint? matched = null;
        RouteValueDictionary? values = null;
        bool methodNotAllowed = false;

        foreach (var ep in _endpoints)
        {
            var v = MatchPattern(ep.RoutePattern, path);
            if (v == null) continue;
            var methods = ep.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
            if (methods != null && !methods.Contains(method))
            {
                methodNotAllowed = true;
                continue;
            }
            matched = ep;
            values = v;
            break;
        }

        if (matched == null)
        {
            resp.StatusCode = methodNotAllowed ? 405 : 404;
            return Finalize(resp, respBody);
        }

        var routeData = new RouteData();
        foreach (var (k, v) in values!) routeData.Values[k] = v;
        features.Set<IRoutingFeature>(new RoutingFeature { RouteData = routeData });
        features.Set<IRouteValuesFeature>(new RouteValuesFeature { RouteValues = routeData.Values });
        features.Set<IEndpointFeature>(new EndpointFeature { Endpoint = matched });

        Exception? dispatchError = null;
        try
        {
            await matched.RequestDelegate!(ctx);
        }
        catch (BadHttpRequestException ex)
        {
            dispatchError = ex;
            resp.StatusCode = ex.StatusCode;
            var msg = System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(
                new { error = "BAD_HTTP_REQUEST", message = ex.Message, detail = ex.InnerException?.Message }));
            resp.Body.Write(msg);
        }
        catch (ConflictException ex)
        {
            ctx.Response.StatusCode = 409;
            await ctx.Response.WriteAsJsonAsync(new { error = "REVISION_CONFLICT", message = ex.Message, currentRevision = ex.CurrentRevision });
        }
        catch (DomainException ex)
        {
            ctx.Response.StatusCode = 422;
            await ctx.Response.WriteAsJsonAsync(new { error = ex.Code, message = ex.Message });
        }
        catch (Exception ex) when (resp.StatusCode == 400)
        {
            System.Console.WriteLine("DISPATCH400: " + ex);
            throw;
        }
        return Finalize(resp, respBody);
    }

    private static Response Finalize(InProcessResponseFeature resp, InProcessResponseBodyFeature body) =>
        new() { StatusCode = resp.StatusCode, Headers = resp.Headers, Body = body.Bytes };

    private static RouteValueDictionary? MatchPattern(RoutePattern pattern, string path)
    {
        var pathParts = path.Trim('/').Split('/');
        var segments = pattern.PathSegments;
        if (pathParts.Length != segments.Count) return null;

        var values = new RouteValueDictionary();
        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            if (seg.Parts.Count != 1) return null;
            var part = seg.Parts[0];
            switch (part)
            {
                case RoutePatternLiteralPart lit:
                    if (!string.Equals(lit.Content, pathParts[i], StringComparison.OrdinalIgnoreCase))
                        return null;
                    break;
                case RoutePatternParameterPart par:
                    values[par.Name] = Uri.UnescapeDataString(pathParts[i]);
                    break;
                default:
                    return null;
            }
        }
        return values;
    }

    private sealed class RoutingFeature : IRoutingFeature
    {
        public required RouteData RouteData { get; set; }
    }

    private sealed class RouteValuesFeature : IRouteValuesFeature
    {
        public required RouteValueDictionary RouteValues { get; set; }
    }

    private sealed class EndpointFeature : IEndpointFeature
    {
        public Endpoint? Endpoint { get; set; }
    }
}

internal sealed class BodyPipeFeature : IRequestBodyPipeFeature, IDisposable
{
    public PipeReader Reader { get; }
    private readonly Pipe _pipe = new();

    public BodyPipeFeature(Stream body)
    {
        Reader = _pipe.Reader;
        // Stream and PipeReader share the same pipe data.
        body.Position = 0;
        byte[] bytes = ((MemoryStream)body).ToArray();
        _pipe.Writer.WriteAsync(new ReadOnlyMemory<byte>(bytes)).AsTask().GetAwaiter().GetResult();
        _pipe.Writer.Complete();
    }

    public void Dispose()
    {
        _pipe.Reader.Complete();
    }
}

internal sealed class SyncIOControl : IHttpBodyControlFeature
{
    public bool AllowSynchronousIO { get; set; }
}

internal sealed class MaxBody : Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature
{
    public bool IsReadOnly => false;
    public long? MaxRequestBodySize { get; set; } = 200_000_000;
}

internal sealed class TestTrailers : Microsoft.AspNetCore.Http.Features.IHttpResponseTrailersFeature
{
    public IHeaderDictionary Trailers { get; set; } = new HeaderDictionary();
}

internal sealed class TestReset : Microsoft.AspNetCore.Http.Features.IHttpResetFeature
{
    public void Reset(int errorCode) { }
}

internal sealed class BodyDetection : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature
{
    public bool CanHaveBody => true;
}

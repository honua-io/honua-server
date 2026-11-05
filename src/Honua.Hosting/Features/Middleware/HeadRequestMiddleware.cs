// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IO.Pipelines;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;

namespace Honua.Infrastructure.Middleware;

// Response metadata is not a reliable stream marker: some transports negotiate several representations on one route, and the MCP GET stream owns its response contract outside MVC and OpenAPI metadata.
public sealed class LongLivedStreamEndpointMetadata
{
    private LongLivedStreamEndpointMetadata()
    {
    }

    public static LongLivedStreamEndpointMetadata Instance { get; } = new();
}

public sealed class WebSocketEndpointMetadata
{
    private WebSocketEndpointMetadata()
    {
    }

    public static WebSocketEndpointMetadata Instance { get; } = new();
}

// A GET whose handler has side effects must not run under the HEAD-to-GET fallback.
internal sealed class HeadRequestRejectedEndpointMetadata
{
    private readonly Func<HttpContext, bool> _shouldReject;

    public HeadRequestRejectedEndpointMetadata(IReadOnlyList<string> allowedMethods)
        : this(allowedMethods, static _ => true)
    {
    }

    // Only the selected GET-compatible operation rejects HEAD; other GETs on the same endpoint still run.
    public HeadRequestRejectedEndpointMetadata(
        IReadOnlyList<string> allowedMethods,
        Func<HttpContext, bool> shouldReject)
    {
        ArgumentNullException.ThrowIfNull(allowedMethods);
        ArgumentNullException.ThrowIfNull(shouldReject);
        if (allowedMethods.Count == 0 || allowedMethods.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one non-empty allowed method is required.", nameof(allowedMethods));
        }

        AllowedMethods = [.. allowedMethods];
        _shouldReject = shouldReject;
    }

    public IReadOnlyList<string> AllowedMethods { get; }

    public bool ShouldReject(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _shouldReject(context);
    }
}

public sealed class ExplicitHeadOnlyEndpointMetadata
{
    public ExplicitHeadOnlyEndpointMetadata(IReadOnlyList<string> declaredMethods)
    {
        ArgumentNullException.ThrowIfNull(declaredMethods);
        DeclaredMethods = [.. declaredMethods];
    }

    public IReadOnlyList<string> DeclaredMethods { get; }
}

// HEAD is rewritten to GET only so routing can select the GET endpoint, then restored before authorization, and the handler still sees GET unless it maps HEAD because several handlers treat any other method as a body read.
internal static class HeadRequestSupport
{
    internal static readonly object RewrittenFromHeadKey = new();

    internal static bool WasRewrittenFromHead(HttpContext context)
        => context.Items.ContainsKey(RewrittenFromHeadKey);

    // A 1xx, 204, or 304 response must not gain a Content-Length the equivalent GET would not have sent.
    internal static bool CanCarryContentLength(int statusCode)
        => statusCode is not (>= StatusCodes.Status100Continue and < StatusCodes.Status200OK)
            and not StatusCodes.Status204NoContent
            and not StatusCodes.Status304NotModified;

    internal const string EventStreamContentType = "text/event-stream";

    // A long-lived stream's HEAD byte count is only the preamble and must not be advertised as Content-Length.
    internal static readonly object SuppressSynthesizedContentLengthKey = new();

    internal static bool SuppressesSynthesizedContentLength(HttpContext context)
        => context.Items.ContainsKey(SuppressSynthesizedContentLengthKey);

    internal static readonly object ResponseBodyFeatureKey = new();

    internal static HeadResponseBodyFeature? GetResponseBodyFeature(HttpContext context)
        => context.Items.TryGetValue(ResponseBodyFeatureKey, out var feature)
            ? feature as HeadResponseBodyFeature
            : null;

    internal static bool IsLongLivedStreamEndpoint(Endpoint? endpoint)
    {
        var metadata = endpoint?.Metadata;
        if (metadata is null)
        {
            return false;
        }

        if (metadata.GetMetadata<LongLivedStreamEndpointMetadata>() is not null)
        {
            return true;
        }

        return metadata
            .OfType<IProducesResponseTypeMetadata>()
            .SelectMany(static produces => produces.ContentTypes)
            .Any(static contentType =>
                contentType.StartsWith(EventStreamContentType, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class HeadRequestStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return app =>
        {
            app.UseMiddleware<HeadRequestRewriteMiddleware>();
            next(app);
        };
    }
}

internal sealed class HeadRequestRewriteMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        context.Items[HeadRequestSupport.RewrittenFromHeadKey] = true;
        context.Request.Method = HttpMethods.Get;

        var originalResponseFeature = context.Features.GetRequiredFeature<IHttpResponseFeature>();
        var logicalResponseFeature = new HeadResponseFeature(originalResponseFeature);
        var originalBodyFeature = context.Features.GetRequiredFeature<IHttpResponseBodyFeature>();
        using var discardingBodyFeature = new HeadResponseBodyFeature(logicalResponseFeature.MarkStarted);
        context.Items[HeadRequestSupport.ResponseBodyFeatureKey] = discardingBodyFeature;
        context.Features.Set<IHttpResponseFeature>(logicalResponseFeature);
        context.Features.Set<IHttpResponseBodyFeature>(discardingBodyFeature);

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            // Restore the method for anything that inspects it while the pipeline unwinds
            // (request logging is registered downstream of the restoration middleware, but
            // a short-circuit inside routing would never have reached that middleware).
            context.Request.Method = HttpMethods.Head;

            var bytesWritten = await discardingBodyFeature.FinishAsync().ConfigureAwait(false);
            context.Features.Set<IHttpResponseFeature>(originalResponseFeature);
            context.Features.Set<IHttpResponseBodyFeature>(originalBodyFeature);
            context.Items.Remove(HeadRequestSupport.ResponseBodyFeatureKey);
            if (!context.Response.HasStarted &&
                HeadRequestSupport.CanCarryContentLength(context.Response.StatusCode))
            {
                if (context.Response.Headers.ContainsKey(HeaderNames.TransferEncoding))
                {
                    // RFC 9112 section 6.3 forbids sending Content-Length together with
                    // Transfer-Encoding. Streaming query handlers select chunked framing and
                    // some hosts infer a length from a completed buffered write, so actively
                    // remove either a handler- or host-provided length as well as declining to
                    // synthesize one here.
                    context.Response.ContentLength = null;
                }
                else if (context.Response.ContentLength is null &&
                         !HeadRequestSupport.SuppressesSynthesizedContentLength(context))
                {
                    context.Response.ContentLength = bytesWritten;
                }

                // RFC 9110 section 14.3: "A server that does not support any kind of range
                // request for the target resource MAY send Accept-Ranges: none to advise the
                // client not to attempt a range request."
                //
                // This is load-bearing, not decorative. GDAL's /vsicurl decides whether to
                // read a resource in ranged chunks from what a HEAD reveals: before HEAD was
                // supported it could not learn a Content-Length and fell back to a single
                // streaming GET, but a HEAD that advertises a length and stays silent about
                // ranges reads to /vsicurl as "ranges are available". Honua answers a Range
                // request with a full 200 and no Content-Range, so every chunk /vsicurl asked
                // for came back as the whole document at the wrong offset and the dataset
                // failed to open outright — DuckDB Spatial's ST_Read over a plain items URL
                // regressed from green to "Could not open GDAL dataset" the moment HEAD
                // started working (honua-server#3389). Declaring `none` restores the
                // streaming read.
                //
                // Stamped whenever the response is silent about ranges, NOT only when this
                // middleware synthesized the length. The handlers that serve a whole buffer
                // through `Results.File(byte[])` -- WCS GetCoverage, OGC Maps, ImageServer
                // exportImage -- set their own Content-Length and never set Accept-Ranges, so
                // gating on a synthesized length left exactly the Content-Length-plus-no-range
                // shape described above in place for the binary raster responses /vsicurl is
                // most likely to probe (review finding on honua-server#3489). An endpoint that
                // genuinely serves ranges (the PMTiles range proxy, scene assets) sets its own
                // Accept-Ranges, and the ContainsKey guard below preserves that value.
                if (!context.Response.Headers.ContainsKey(HeaderNames.AcceptRanges))
                {
                    context.Response.Headers[HeaderNames.AcceptRanges] = "none";
                }
            }
        }
    }
}

internal sealed class ExplicitHeadEndpointMatcherPolicy : MatcherPolicy, IEndpointSelectorPolicy
{
    public override int Order => 0;

    public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints)
        => endpoints.Any(static endpoint =>
            endpoint.Metadata.GetMetadata<ExplicitHeadOnlyEndpointMetadata>() is not null);

    public Task ApplyAsync(HttpContext httpContext, CandidateSet candidates)
    {
        var rewrittenFromHead = HeadRequestSupport.WasRewrittenFromHead(httpContext);
        var hasExplicitHeadCandidate = false;
        HashSet<string>? allowedMethods = null;
        var hasOrdinaryValidCandidate = false;

        for (var i = 0; i < candidates.Count; i++)
        {
            var endpoint = candidates[i].Endpoint;
            var metadata = endpoint.Metadata.GetMetadata<ExplicitHeadOnlyEndpointMetadata>();
            if (metadata is not null)
            {
                allowedMethods ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var method in metadata.DeclaredMethods)
                {
                    allowedMethods.Add(method);
                }
            }
            else if (endpoint.Metadata.GetMetadata<HttpMethodMetadata>() is { } httpMethodMetadata)
            {
                // Method matching invalidates POST/PUT/etc. candidates before this policy runs,
                // but they still belong in the RFC 9110 Allow header for the matched route.
                allowedMethods ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var method in httpMethodMetadata.HttpMethods)
                {
                    allowedMethods.Add(method);
                }
            }

            if (!candidates.IsValidCandidate(i))
            {
                continue;
            }

            if (metadata is null)
            {
                hasOrdinaryValidCandidate = true;
                continue;
            }

            hasExplicitHeadCandidate = true;
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            if (!candidates.IsValidCandidate(i))
            {
                continue;
            }

            var isExplicitHeadOnly =
                candidates[i].Endpoint.Metadata.GetMetadata<ExplicitHeadOnlyEndpointMetadata>() is not null;

            if ((rewrittenFromHead && hasExplicitHeadCandidate && !isExplicitHeadOnly) ||
                (!rewrittenFromHead && isExplicitHeadOnly))
            {
                candidates.SetValidity(i, false);
            }
        }

        if (!rewrittenFromHead &&
            hasExplicitHeadCandidate &&
            !hasOrdinaryValidCandidate)
        {
            httpContext.SetEndpoint(CreateMethodNotAllowedEndpoint(allowedMethods));
        }

        return Task.CompletedTask;
    }

    private static Endpoint CreateMethodNotAllowedEndpoint(IEnumerable<string>? methods)
    {
        var allow = methods is null
            ? string.Empty
            : string.Join(", ", methods.Order(StringComparer.OrdinalIgnoreCase));

        return new Endpoint(
            context =>
            {
                context.Response.Headers.Allow = allow;
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return Task.CompletedTask;
            },
            EndpointMetadataCollection.Empty,
            "405 HTTP Method Not Supported (explicit HEAD plugin route)");
    }
}

internal sealed class HeadResponseFeature(IHttpResponseFeature inner) : IHttpResponseFeature
{
    private bool _logicallyStarted;

    public int StatusCode
    {
        get => inner.StatusCode;
        set => inner.StatusCode = value;
    }

    public string? ReasonPhrase
    {
        get => inner.ReasonPhrase;
        set => inner.ReasonPhrase = value;
    }

    public IHeaderDictionary Headers
    {
        get => inner.Headers;
        set => inner.Headers = value;
    }

#pragma warning disable CS0618 // Required to delegate the legacy IHttpResponseFeature contract.
    public Stream Body
    {
        get => inner.Body;
        set => inner.Body = value;
    }
#pragma warning restore CS0618

    public bool HasStarted => _logicallyStarted || inner.HasStarted;

    public void OnStarting(Func<object, Task> callback, object state) => inner.OnStarting(callback, state);

    public void OnCompleted(Func<object, Task> callback, object state) => inner.OnCompleted(callback, state);

    internal void MarkStarted() => _logicallyStarted = true;
}

internal sealed class HeadRequestMethodRestorationMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (HeadRequestSupport.WasRewrittenFromHead(context))
        {
            context.Request.Method = HttpMethods.Head;
        }

        return _next(context);
    }
}

internal sealed class HeadRequestGetSemanticsMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var endpoint = context.GetEndpoint();

        if (!HeadRequestSupport.WasRewrittenFromHead(context))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (endpoint?.Metadata.GetMetadata<HeadRequestRejectedEndpointMetadata>() is { } rejected &&
            rejected.ShouldReject(context))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = string.Join(", ", rejected.AllowedMethods);
            await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (AdvertisesHead(endpoint))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (endpoint?.Metadata.GetMetadata<WebSocketEndpointMetadata>() is not null &&
            IsWebSocketUpgradeAttempt(context.Request))
        {
            // A valid WebSocket handshake is GET. Reject an upgrade-shaped HEAD centrally:
            // WebSocket frames bypass the suppressed response-body feature, and allowing a
            // dual-transport handler to evaluate alternate Accept headers could select an SSE
            // loop after WebSocket negotiation fails and retain a bounded stream session.
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        context.Request.Method = HttpMethods.Get;
        var hadRangeHeader = context.Request.Headers.ContainsKey(HeaderNames.Range);
        var originalRangeHeader = context.Request.Headers[HeaderNames.Range];
        context.Request.Headers.Remove(HeaderNames.Range);

        try
        {
            if (HeadRequestSupport.IsLongLivedStreamEndpoint(endpoint))
            {
                await InvokeBoundedStreamAsync(context).ConfigureAwait(false);
            }
            else
            {
                await _next(context).ConfigureAwait(false);
            }
        }
        finally
        {
            if (hadRangeHeader)
            {
                context.Request.Headers[HeaderNames.Range] = originalRangeHeader;
            }

            // Put HEAD back on the way out so the upstream middleware that reports on the
            // completed request (Serilog request logging, performance monitoring, auditing)
            // records the method the client actually sent.
            context.Request.Method = HttpMethods.Head;
        }
    }

    private static bool IsWebSocketUpgradeAttempt(HttpRequest request)
        => HeaderContainsToken(request.Headers[HeaderNames.Connection], "upgrade") &&
           HeaderContainsToken(request.Headers[HeaderNames.Upgrade], "websocket");

    private static bool HeaderContainsToken(IEnumerable<string?> values, string expected)
        => values.Any(value => value is not null && value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(expected, StringComparer.OrdinalIgnoreCase));

    // A streaming handler is cancelled at its first response activity so HEAD cannot hold a session until the client times out, while an early 404, 503, or 400 still surfaces.
    private async Task InvokeBoundedStreamAsync(HttpContext context)
    {
        // The equivalent GET is unbounded and sends no Content-Length; the preamble byte count
        // observed here must not be stamped as one.
        context.Items[HeadRequestSupport.SuppressSynthesizedContentLengthKey] = true;

        var clientToken = context.RequestAborted;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(clientToken);
        // Response compression and similar middleware decorate IHttpResponseBodyFeature before
        // this stage runs. Retrieve the original feature retained by the rewrite middleware so
        // response activity still releases a bounded stream through any wrapper chain.
        var headBody = HeadRequestSupport.GetResponseBodyFeature(context);

        if (headBody is not null)
        {
            headBody.FirstResponseActivityCallback = () =>
            {
                try
                {
                    bounded.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The request finished on its own between the write and this callback.
                }
            };
        }

        context.RequestAborted = bounded.Token;

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (bounded.IsCancellationRequested && !clientToken.IsCancellationRequested)
        {
            // The handler observed the bound this middleware imposed, not a client disconnect.
            // Its headers are already on the response; ending quietly is the whole point.
        }
        finally
        {
            if (headBody is not null)
            {
                headBody.FirstResponseActivityCallback = null;
            }

            context.RequestAborted = clientToken;
        }
    }

    private static bool AdvertisesHead(Endpoint? endpoint)
    {
        if (endpoint?.Metadata.GetMetadata<ExplicitHeadOnlyEndpointMetadata>() is not null)
        {
            return true;
        }

        var httpMethods = endpoint?.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        if (httpMethods is null)
        {
            return false;
        }

        for (var i = 0; i < httpMethods.Count; i++)
        {
            if (HttpMethods.IsHead(httpMethods[i]))
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class HeadResponseBodyFeature : IHttpResponseBodyFeature, IDisposable
{
    private readonly CountingNullStream _stream;
    private PipeWriter? _writer;
    private bool _finished;

    internal HeadResponseBodyFeature(Action responseStarted)
    {
        ArgumentNullException.ThrowIfNull(responseStarted);
        _stream = new CountingNullStream(responseStarted);
    }

    public Stream Stream => _stream;

    public PipeWriter Writer =>
        _writer ??= PipeWriter.Create(_stream, new StreamPipeWriterOptions(leaveOpen: true));

    public long BytesWritten => _stream.BytesWritten;

    internal Action? FirstResponseActivityCallback
    {
        get => _stream.FirstResponseActivityCallback;
        set => _stream.FirstResponseActivityCallback = value;
    }

    public void DisableBuffering()
    {
        // Nothing is buffered towards the transport; there is nothing to disable.
    }

    // The real response is not started here so Content-Length can still be set after the endpoint completes.
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _stream.SignalResponseActivity();
        return Task.CompletedTask;
    }

    public Task CompleteAsync()
    {
        _stream.SignalResponseActivity();
        return FinishAsync().AsTask();
    }

    public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        long length;
        if (count.HasValue)
        {
            length = count.Value;
        }
        else
        {
            var fileInfo = new FileInfo(path);
            length = fileInfo.Exists ? Math.Max(0L, fileInfo.Length - offset) : 0L;
        }

        _stream.AddUntransferredBytes(length);
        return Task.CompletedTask;
    }

    public void Dispose() => _stream.Dispose();

    internal async ValueTask<long> FinishAsync()
    {
        if (!_finished)
        {
            _finished = true;

            if (_writer is not null)
            {
                // CompleteAsync (not FlushAsync) so a writer another component already
                // completed is a no-op instead of an InvalidOperationException.
                await _writer.CompleteAsync().ConfigureAwait(false);
            }
        }

        return _stream.BytesWritten;
    }

    private sealed class CountingNullStream : Stream
    {
        private readonly Action _responseStarted;
        private long _bytesWritten;
        private Action? _firstResponseActivityCallback;
        private bool _firstResponseActivitySignalled;

        internal CountingNullStream(Action responseStarted)
        {
            _responseStarted = responseStarted;
        }

        public long BytesWritten => _bytesWritten;

        public Action? FirstResponseActivityCallback
        {
            get => _firstResponseActivityCallback;
            set => _firstResponseActivityCallback = value;
        }

        internal void SignalResponseActivity()
        {
            if (_firstResponseActivitySignalled)
            {
                return;
            }

            _firstResponseActivitySignalled = true;
            _responseStarted();
            _firstResponseActivityCallback?.Invoke();
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _bytesWritten;
            set => throw new NotSupportedException();
        }

        public void AddUntransferredBytes(long count)
        {
            _bytesWritten += count;
            SignalResponseActivity();
        }

        public override void Flush()
        {
            SignalResponseActivity();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            SignalResponseActivity();
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _bytesWritten += count;
            SignalResponseActivity();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _bytesWritten += buffer.Length;
            SignalResponseActivity();
        }

        public override void WriteByte(byte value)
        {
            _bytesWritten++;
            SignalResponseActivity();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            _bytesWritten += count;
            SignalResponseActivity();
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _bytesWritten += buffer.Length;
            SignalResponseActivity();
            return ValueTask.CompletedTask;
        }
    }
}

internal static class HeadRequestMiddlewareExtensions
{
    // Register this on the builder services so the startup filter is in place before implicit routing is built.
    public static IServiceCollection AddHonuaHeadRequestSupport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IStartupFilter, HeadRequestStartupFilter>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<MatcherPolicy, ExplicitHeadEndpointMatcherPolicy>());
        return services;
    }

    public static IApplicationBuilder UseHonuaHeadRequestMethod(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<HeadRequestMethodRestorationMiddleware>();
    }

    public static IApplicationBuilder UseHonuaHeadRequestGetSemantics(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<HeadRequestGetSemanticsMiddleware>();
    }
}

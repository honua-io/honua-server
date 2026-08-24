// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO.Pipelines;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Net.Http.Headers;

namespace Honua.Infrastructure.Middleware;

/// <summary>
/// Cross-cutting RFC 9110 §9.3.2 support: <c>HEAD</c> is answered wherever <c>GET</c> is,
/// with the same status code and headers (including <c>Content-Length</c>) but no body.
/// </summary>
/// <remarks>
/// <para>
/// Bug (client-certification epic #3389): the server mapped <c>HEAD</c> on exactly two
/// routes (<c>/scenes/*</c> and <c>/api/v1/tiles/pmtiles/*</c>). Every other GET route
/// answered <c>HEAD</c> with 404 or 405 — <c>/healthz/ready</c>, <c>/ogc/features</c>,
/// <c>/ogc/features/collections</c>, <c>/wfs?REQUEST=GetCapabilities</c>, <c>/stac</c>,
/// <c>/api/v1/admin/*</c> — because ASP.NET Core routing never falls back from HEAD to
/// GET: <c>MapMethods(path, [HttpMethods.Get], …)</c> matches GET only, so a HEAD request
/// either matches nothing (404) or matches the path but no method (405). That broke real
/// clients (GDAL <c>/vsicurl</c> and its OAPIF/WFS drivers, <c>requests.head</c>, OWSLib,
/// link checkers, CDNs, reverse proxies) and it silently stalled the entire client-interop
/// nightly matrix, whose compose healthcheck probed <c>/healthz/ready</c> with
/// <c>wget --spider</c> (a real HEAD) and therefore never reported the container healthy.
/// </para>
/// <para>
/// Design — a three-part middleware around routing, implemented once here rather than by
/// adding <c>HEAD</c> to every <c>MapMethods</c> call site:
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <see cref="HeadRequestRewriteMiddleware"/> runs <em>before</em> routing (installed via
/// <see cref="HeadRequestStartupFilter"/>, because <see cref="WebApplication"/> inserts its
/// implicit <c>UseRouting</c> ahead of every middleware registered in <c>Program.cs</c>, and
/// an <c>IStartupFilter</c> is the only seam that precedes it without moving endpoint
/// matching after the whole pipeline). It rewrites the request method to <c>GET</c> so the
/// GET endpoint is selected, and replaces the response body feature with a write-discarding,
/// byte-counting one.
/// </description>
/// </item>
/// <item>
/// <description>
/// <see cref="HeadRequestMethodRestorationMiddleware"/> runs immediately after routing has
/// selected the endpoint and restores the method to <c>HEAD</c>, so every cross-cutting
/// component observes the method the client actually sent: authorization
/// (<c>AdminApiKeyPermission</c> treats HEAD as a read), tenancy, auditing, rate limiting,
/// telemetry and request logging.
/// </description>
/// </item>
/// <item>
/// <description>
/// <see cref="HeadRequestGetSemanticsMiddleware"/> runs last, immediately before endpoint
/// execution, and hands the *handler* whichever method it was written for: <c>HEAD</c> when
/// the matched endpoint advertises HEAD itself (the PMTiles range proxy and the scene
/// endpoints map <c>["GET", "HEAD"]</c> and short-circuit on it to avoid streaming a
/// payload), otherwise <c>GET</c>. A GET-only handler has, by definition, no HEAD code path,
/// and several of them read the request body whenever the method is not GET
/// (<c>GeoServicesRequestValueHelpers.TryReadRequestValuesAsync</c>,
/// <c>FeatureServer/MapServer generateRenderer</c>, the import endpoints); showing them
/// <c>HEAD</c> would answer 400/415 where GET answers 200. Response-cache decisions
/// (<c>ResponseCacheUtilities.ShouldCache</c>) are GET-gated the same way, so this is also
/// what keeps the HEAD response's cache headers identical to the GET's — which is what
/// RFC 9110's "identical to GET" requires.
/// </description>
/// </item>
/// <item>
/// <description>
/// When the endpoint completes, the counted byte total becomes <c>Content-Length</c> unless
/// the handler set one itself (the PMTiles HEAD branch does) or the response has already
/// started. Discarding the bytes instead of letting the handler write them is what keeps the
/// response body empty on hosts that do not suppress it themselves (<c>TestServer</c>);
/// Kestrel additionally suppresses HEAD bodies at the transport level, since rewriting
/// <c>HttpRequest.Method</c> only changes the request-feature text and not the parsed
/// <c>HttpMethod</c> Kestrel uses for that decision.
/// </description>
/// </item>
/// </list>
/// <para>
/// Rejected alternative: a startup convention that appends <c>HEAD</c> to every endpoint
/// whose <c>HttpMethodMetadata</c> contains <c>GET</c>. It preserves <c>Request.Method</c>
/// naturally, but it has to rebuild every endpoint from every endpoint data source
/// (health checks, gRPC, static assets, dynamic sources) and, more importantly, it leaves
/// the body suppression entirely to the server: Kestrel would answer HEAD chunked with no
/// <c>Content-Length</c> at all for the streamed JSON that most Honua endpoints produce,
/// and <c>TestServer</c> would return the full body. Clients probe HEAD precisely to learn
/// <c>Content-Length</c>/<c>Content-Type</c>, so that is the wrong trade.
/// </para>
/// <para>
/// What is intentionally unchanged: genuine 405s (HEAD on a POST-only route still fails
/// method matching, because the rewritten GET does not match either), 404s for unknown
/// paths, and the explicit "method not allowed" routes that answer POST/PUT/DELETE/PATCH.
/// A HEAD request does execute the GET handler in full (its output is discarded), which is
/// what makes <c>Content-Length</c> exact; handlers with an expensive body can still opt
/// out by mapping HEAD themselves and short-circuiting, as the PMTiles proxy does.
/// </para>
/// <para>
/// Consequences worth knowing: the request is logged, audited and metered as <c>HEAD</c>, but
/// the handler and its endpoint filters observe <c>GET</c> unless the endpoint maps HEAD, so
/// <c>Request.Method</c> is not a reliable HEAD signal inside a GET-only handler — use
/// <see cref="HeadRequestSupport.WasRewrittenFromHead"/> if a handler ever needs to know.
/// </para>
/// </remarks>
internal static class HeadRequestSupport
{
    /// <summary>
    /// Marks a request whose method was rewritten from HEAD to GET for endpoint matching.
    /// An object key cannot collide with the string keys other features put in
    /// <see cref="HttpContext.Items"/>.
    /// </summary>
    internal static readonly object RewrittenFromHeadKey = new();

    internal static bool WasRewrittenFromHead(HttpContext context)
        => context.Items.ContainsKey(RewrittenFromHeadKey);

    /// <summary>
    /// A 1xx, 204 or 304 response carries no body and must not gain a synthesized
    /// <c>Content-Length: 0</c> that the equivalent GET would not have sent.
    /// </summary>
    internal static bool CanCarryContentLength(int statusCode)
        => statusCode is not (>= StatusCodes.Status100Continue and < StatusCodes.Status200OK)
            and not StatusCodes.Status204NoContent
            and not StatusCodes.Status304NotModified;

    /// <summary>The media type that identifies a Server-Sent Events response.</summary>
    internal const string EventStreamContentType = "text/event-stream";

    /// <summary>
    /// Set when the matched endpoint is a long-lived stream. The equivalent GET never
    /// completes on its own, so the byte count a bounded HEAD probe observed is the size of
    /// the preamble rather than of the response, and stamping it as <c>Content-Length</c>
    /// would advertise a length no GET would ever send.
    /// </summary>
    internal static readonly object SuppressSynthesizedContentLengthKey = new();

    internal static bool SuppressesSynthesizedContentLength(HttpContext context)
        => context.Items.ContainsKey(SuppressSynthesizedContentLengthKey);

    /// <summary>
    /// True when the endpoint declares that it produces <c>text/event-stream</c>.
    /// </summary>
    /// <remarks>
    /// Read from the endpoint's own declared response metadata (the <c>Produces</c> call that
    /// already documents it in OpenAPI) rather than from a separate marker attribute, so a
    /// new SSE endpoint is bounded by construction instead of by remembering to annotate it.
    /// </remarks>
    internal static bool IsLongLivedStreamEndpoint(Endpoint? endpoint)
    {
        var metadata = endpoint?.Metadata;
        if (metadata is null)
        {
            return false;
        }

        for (var i = 0; i < metadata.Count; i++)
        {
            if (metadata[i] is not IProducesResponseTypeMetadata produces)
            {
                continue;
            }

            foreach (var contentType in produces.ContentTypes)
            {
                if (contentType.StartsWith(EventStreamContentType, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

/// <summary>
/// Installs <see cref="HeadRequestRewriteMiddleware"/> ahead of the implicit
/// <c>UseRouting</c> that <see cref="WebApplication"/> adds before any middleware
/// registered in <c>Program.cs</c>. See <see cref="HeadRequestSupport"/> for the design.
/// </summary>
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

/// <summary>
/// Pre-routing half of HEAD support: rewrites HEAD to GET so endpoint matching selects the
/// GET endpoint, discards and counts the response body, and stamps the resulting byte count
/// as <c>Content-Length</c>. See <see cref="HeadRequestSupport"/>.
/// </summary>
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

        var originalBodyFeature = context.Features.GetRequiredFeature<IHttpResponseBodyFeature>();
        var discardingBodyFeature = new HeadResponseBodyFeature();
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
            context.Features.Set<IHttpResponseBodyFeature>(originalBodyFeature);
            discardingBodyFeature.Dispose();

            if (!context.Response.HasStarted &&
                HeadRequestSupport.CanCarryContentLength(context.Response.StatusCode))
            {
                if (context.Response.ContentLength is null &&
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

/// <summary>
/// Post-routing half of HEAD support: restores <c>HEAD</c> as the request method once the
/// GET endpoint has been selected. See <see cref="HeadRequestSupport"/>.
/// </summary>
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

/// <summary>
/// Final stage of HEAD support: gives the matched endpoint the method its handler was written
/// for. Endpoints that advertise HEAD keep seeing <c>HEAD</c>; every other endpoint sees the
/// <c>GET</c> it maps, so a HEAD request takes exactly the GET code path. See
/// <see cref="HeadRequestSupport"/>.
/// </summary>
internal sealed class HeadRequestGetSemanticsMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var endpoint = context.GetEndpoint();

        if (!HeadRequestSupport.WasRewrittenFromHead(context) ||
            AdvertisesHead(endpoint))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        context.Request.Method = HttpMethods.Get;

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
            // Put HEAD back on the way out so the upstream middleware that reports on the
            // completed request (Serilog request logging, performance monitoring, auditing)
            // records the method the client actually sent.
            context.Request.Method = HttpMethods.Head;
        }
    }

    /// <summary>
    /// Runs a long-lived streaming handler only as far as its first written byte, then cancels
    /// it.
    /// </summary>
    /// <remarks>
    /// A Server-Sent Events handler commits its status code and headers and writes a preamble,
    /// and only then loops until the client disconnects. Left alone under HEAD that loop never
    /// ends: the discarding body feature swallows every heartbeat, so the transport response is
    /// never started and nothing ever aborts the request. <c>HEAD
    /// /api/v1/realtime/incidents/sse</c> therefore hung until the client timed out, and the
    /// SensorThings observation stream additionally held one of a bounded number of stream
    /// sessions open for that whole time — a HEAD probe from a link checker or a CDN could
    /// exhaust them (review finding on honua-server#3489).
    ///
    /// Bounding at the first write rather than skipping the handler outright is what keeps HEAD
    /// honest: the status code and headers are the ones the handler really produced, so the
    /// early returns that precede the stream still answer as themselves — 404 for an unknown
    /// datastream, 503 when no session can be leased, 400 when the client did not negotiate
    /// SSE — instead of being flattened into a synthetic 200.
    /// </remarks>
    private async Task InvokeBoundedStreamAsync(HttpContext context)
    {
        // The equivalent GET is unbounded and sends no Content-Length; the preamble byte count
        // observed here must not be stamped as one.
        context.Items[HeadRequestSupport.SuppressSynthesizedContentLengthKey] = true;

        var clientToken = context.RequestAborted;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(clientToken);
        var headBody = context.Features.Get<IHttpResponseBodyFeature>() as HeadResponseBodyFeature;

        if (headBody is not null)
        {
            headBody.FirstWriteCallback = () =>
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
                headBody.FirstWriteCallback = null;
            }

            context.RequestAborted = clientToken;
        }
    }

    private static bool AdvertisesHead(Endpoint? endpoint)
    {
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

/// <summary>
/// Response body feature for a HEAD request: accepts every write, forwards none of them to
/// the transport, and counts the bytes the equivalent GET would have sent so the middleware
/// can report an accurate <c>Content-Length</c>.
/// </summary>
internal sealed class HeadResponseBodyFeature : IHttpResponseBodyFeature, IDisposable
{
    private readonly CountingNullStream _stream = new();
    private PipeWriter? _writer;
    private bool _finished;

    public Stream Stream => _stream;

    public PipeWriter Writer =>
        _writer ??= PipeWriter.Create(_stream, new StreamPipeWriterOptions(leaveOpen: true));

    public long BytesWritten => _stream.BytesWritten;

    /// <summary>
    /// Invoked once, on the first byte the handler writes. A long-lived stream commits its
    /// status and headers immediately and only then loops, so the first write is the point at
    /// which a HEAD probe has learned everything the equivalent GET would tell it.
    /// </summary>
    internal Action? FirstWriteCallback
    {
        get => _stream.FirstWriteCallback;
        set => _stream.FirstWriteCallback = value;
    }

    public void DisableBuffering()
    {
        // Nothing is buffered towards the transport; there is nothing to disable.
    }

    /// <summary>
    /// Deliberately does not start the real response: headers must stay mutable so the
    /// middleware can still set <c>Content-Length</c> after the endpoint completes.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task CompleteAsync() => FinishAsync().AsTask();

    /// <summary>
    /// Counts what the file transfer would have sent without reading a single byte of it.
    /// </summary>
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

    /// <summary>
    /// Flushes anything still buffered in the pipe writer into the counter and returns the
    /// total number of bytes the equivalent GET response would have carried.
    /// </summary>
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
        private long _bytesWritten;
        private Action? _firstWriteCallback;
        private bool _firstWriteSignalled;

        public long BytesWritten => _bytesWritten;

        public Action? FirstWriteCallback
        {
            get => _firstWriteCallback;
            set => _firstWriteCallback = value;
        }

        /// <summary>
        /// Fires the first-write callback exactly once. Callbacks run inline on the writing
        /// thread, so this stays allocation-free and never re-enters after the first byte.
        /// </summary>
        private void SignalFirstWrite()
        {
            if (_firstWriteSignalled)
            {
                return;
            }

            _firstWriteSignalled = true;
            _firstWriteCallback?.Invoke();
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
            SignalFirstWrite();
        }

        public override void Flush()
        {
            // No transport to flush to.
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _bytesWritten += count;
            SignalFirstWrite();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _bytesWritten += buffer.Length;
            SignalFirstWrite();
        }

        public override void WriteByte(byte value)
        {
            _bytesWritten++;
            SignalFirstWrite();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            _bytesWritten += count;
            SignalFirstWrite();
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _bytesWritten += buffer.Length;
            SignalFirstWrite();
            return ValueTask.CompletedTask;
        }
    }
}

internal static class HeadRequestMiddlewareExtensions
{
    /// <summary>
    /// Registers the pre-routing HEAD rewrite. Must be called on the builder's services so
    /// the startup filter is in place before <see cref="WebApplication"/> builds its
    /// implicit routing middleware.
    /// </summary>
    public static IServiceCollection AddHonuaHeadRequestSupport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IStartupFilter, HeadRequestStartupFilter>();
        return services;
    }

    /// <summary>
    /// Restores the HEAD method after routing has matched the GET endpoint. Register this as
    /// the first middleware in the application pipeline.
    /// </summary>
    public static IApplicationBuilder UseHonuaHeadRequestMethod(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<HeadRequestMethodRestorationMiddleware>();
    }

    /// <summary>
    /// Presents the selected endpoint's handler with the method it was written for. Register
    /// this as the LAST middleware in the application pipeline, so everything upstream still
    /// sees HEAD and only endpoint execution sees the substituted GET.
    /// </summary>
    public static IApplicationBuilder UseHonuaHeadRequestGetSemantics(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<HeadRequestGetSemanticsMiddleware>();
    }
}

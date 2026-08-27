// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Infrastructure.Middleware;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Tests.Infrastructure.Middleware;

/// <summary>
/// Focused tests for the shared HEAD middleware (#3389) on a minimal <see cref="WebApplication"/>.
/// They pin the two behaviours the server-wide integration tests cannot isolate: that a route
/// which maps HEAD itself still sees <c>HEAD</c> in the handler (the PMTiles range proxy and the
/// scene endpoints branch on it to avoid streaming a payload) and that a handler-provided
/// <c>Content-Length</c> is never overwritten. They also prove the placement assumption the
/// design rests on — that <see cref="WebApplication"/> runs its implicit <c>UseRouting</c> before
/// any middleware registered on the app, so the startup filter is what gets in front of matching.
/// </summary>
[Protocol(TestProtocols.Infrastructure)]
public sealed class HeadRequestMiddlewareTests : IAsyncLifetime
{
    private const long DualEndpointContentLength = 4096;
    private const int FixedLengthPayloadSize = 64;

    /// <summary>
    /// Long enough that a genuinely unbounded handler cannot pass by luck, short enough that a
    /// regression fails the test instead of hanging the suite.
    /// </summary>
    private static readonly TimeSpan StreamProbeTimeout = TimeSpan.FromSeconds(20);

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    /// <summary>
    /// The request method observed by the stand-in cross-cutting middleware as the pipeline
    /// unwinds. xUnit constructs a fresh instance per test method, so this needs no
    /// cross-test synchronisation.
    /// </summary>
    private string? _upstreamMethodOnUnwind;

    /// <summary>
    /// Completed by the streaming route when its loop unwinds, so a test can prove the handler
    /// was actually released rather than merely that the response came back.
    /// </summary>
    private readonly TaskCompletionSource<bool> _streamHandlerExited =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddHonuaHeadRequestSupport();
        builder.Services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["text/event-stream"]);
        });

        _app = builder.Build();
        _app.UseHonuaHeadRequestMethod();

        // Stands in for the server's cross-cutting middleware (request logging, auditing,
        // telemetry, rate limiting): everything upstream of endpoint execution must observe the
        // method the client actually sent.
        //
        // The unwind observation is recorded into a field rather than a response header on
        // purpose. Once an endpoint has streamed a body the response has started and its
        // headers are immutable, so writing one here would throw for every route that returns
        // content — which is what the real cross-cutting middleware does too: it logs and
        // emits telemetry on the unwind, it does not mutate the response.
        _app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Upstream-Method"] = context.Request.Method;
            await next(context);
            _upstreamMethodOnUnwind = context.Request.Method;
        });

        // Production installs response compression between method restoration and final HEAD
        // semantics. It decorates IHttpResponseBodyFeature when the client negotiates an
        // encoding, so the bounded-stream callback must not depend on a direct feature cast.
        _app.UseResponseCompression();

        // Registered last, exactly as Program.cs does, so it is the final middleware before the endpoint runs.
        _app.UseHonuaHeadRequestGetSemantics();

        // GET-only: the shape of nearly every Honua route before #3389.
        _app.MapGet("/text", async context =>
        {
            context.Response.Headers["X-Method-Seen"] = context.Request.Method;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("hello head");
        });

        // GET + HEAD, branching on the method: the PMTiles/scene shape, which must not regress
        // into fetching the whole payload for a HEAD.
        _app.MapMethods("/dual", ["GET", "HEAD"], async context =>
        {
            context.Response.Headers["X-Method-Seen"] = context.Request.Method;
            context.Response.ContentType = "application/vnd.pmtiles";

            if (HttpMethods.IsHead(context.Request.Method))
            {
                context.Response.ContentLength = DualEndpointContentLength;
                return;
            }

            await context.Response.WriteAsync("payload-streamed-for-get");
        });

        // Enterprise custom endpoints may register independent GET and HEAD handlers for the
        // same pattern. The pre-routing GET fallback must not hide the explicit HEAD endpoint.
        _app.MapGet("/split", async context =>
        {
            context.Response.Headers["X-Handler-Seen"] = "GET";
            await context.Response.WriteAsync("get-handler-payload");
        });
        _app.MapMethods("/split", ["HEAD"], context =>
        {
            context.Response.Headers["X-Handler-Seen"] = "HEAD";
            context.Response.ContentLength = DualEndpointContentLength;
            return Task.CompletedTask;
        });
        _app.MapMethods("/split", ["GET"], context =>
        {
            context.Response.Headers["X-Handler-Seen"] = "HEAD";
            context.Response.ContentLength = DualEndpointContentLength;
            return Task.CompletedTask;
        }).WithMetadata(new ExplicitHeadOnlyEndpointMetadata(["HEAD"]));

        _app.MapMethods("/head-only", ["HEAD"], () => Results.StatusCode(StatusCodes.Status202Accepted));
        _app.MapMethods("/head-only", ["GET"], () => Results.StatusCode(StatusCodes.Status202Accepted))
            .WithMetadata(new ExplicitHeadOnlyEndpointMetadata(["HEAD"]));

        _app.MapPost("/post-only", () => Results.Ok(new { ok = true }));
        _app.MapGet("/no-content", () => Results.NoContent());

        // A long-lived Server-Sent Events route: commits headers, writes a preamble, then loops
        // until the client disconnects. Under HEAD the discarding body feature swallows every
        // heartbeat, so nothing ever aborts the request and the loop would run forever.
        _app.MapGet("/stream", async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("retry: 3000\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);

            try
            {
                while (!context.RequestAborted.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), context.RequestAborted);
                    await context.Response.WriteAsync(": heartbeat\n\n", context.RequestAborted);
                }
            }
            finally
            {
                _streamHandlerExited.TrySetResult(true);
            }
        }).WithMetadata(new ProducesResponseTypeMetadata(
            StatusCodes.Status200OK,
            contentTypes: ["text/event-stream"]));

        // Some transport endpoints do not publish MVC response metadata. MCP in particular
        // commits the SSE response with a flush, then waits for the first notification without
        // writing a byte. The explicit marker and flush boundary must still release HEAD.
        _app.MapGet("/marked-flush-stream", async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.Body.FlushAsync(context.RequestAborted);

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }
            finally
            {
                _streamHandlerExited.TrySetResult(true);
            }
        }).WithMetadata(LongLivedStreamEndpointMetadata.Instance);

        // The same SSE contract, but the request is rejected before the stream opens. HEAD must
        // report that real status rather than a synthetic 200.
        _app.MapGet("/stream-missing", () => Results.NotFound())
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .Produces(StatusCodes.Status404NotFound);

        // The `Results.File(byte[])` shape used by WCS GetCoverage, OGC Maps and ImageServer
        // exportImage: the handler sets its own Content-Length and says nothing about ranges.
        _app.MapGet("/fixed-length", async context =>
        {
            var payload = new byte[FixedLengthPayloadSize];
            context.Response.ContentType = "image/png";
            context.Response.ContentLength = payload.Length;
            await context.Response.Body.WriteAsync(payload);
        });

        // An endpoint that genuinely serves ranges must keep its own advertisement.
        _app.MapGet("/ranged", async context =>
        {
            context.Response.ContentType = "application/octet-stream";
            context.Response.Headers.AcceptRanges = "bytes";
            await context.Response.WriteAsync("ranged-payload");
        });

        // Streaming query handlers explicitly select chunked framing. Their HEAD equivalent
        // must not gain a counted Content-Length alongside Transfer-Encoding.
        _app.MapGet("/chunked", async context =>
        {
            context.Response.ContentType = "application/json";
            context.Response.Headers.TransferEncoding = "chunked";
            await context.Response.WriteAsync("{\"value\":true}");
        });

        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _app.DisposeAsync();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path)
    {
        using var request = new HttpRequestMessage(method, path);
        return await _client.SendAsync(request);
    }

    [UnitTest]
    public async Task InvokeAsync_HeadOnGetOnlyRoute_Returns200WithGetContentLengthAndNoBody()
    {
        using var getResponse = await SendAsync(HttpMethod.Get, "/text");
        var getBody = await getResponse.Content.ReadAsStringAsync();

        using var headResponse = await SendAsync(HttpMethod.Head, "/text");

        headResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await headResponse.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
        headResponse.Content.Headers.ContentType?.ToString().Should().Be("text/plain; charset=utf-8");
        headResponse.Content.Headers.ContentLength.Should().Be(getBody.Length);
    }

    [UnitTest]
    public async Task InvokeAsync_HeadOnGetOnlyRoute_HandlerObservesGetWhileMiddlewareObservesHead()
    {
        using var headResponse = await SendAsync(HttpMethod.Head, "/text");

        headResponse.Headers.GetValues("X-Method-Seen").Should().ContainSingle()
            .Which.Should().Be("GET",
                "a GET-only handler has no HEAD code path; several read the request body whenever " +
                "the method is not GET, so they must take the GET path verbatim");
        headResponse.Headers.GetValues("X-Upstream-Method").Should().ContainSingle()
            .Which.Should().Be("HEAD",
                "auth, auditing, telemetry and request logging must see the method the client sent");
        _upstreamMethodOnUnwind.Should().Be("HEAD",
            "middleware that reports after the endpoint completes must still see HEAD");
    }

    [UnitTest]
    public async Task InvokeAsync_HeadOnRouteThatMapsHead_HandlerStillObservesHeadMethod()
    {
        using var headResponse = await SendAsync(HttpMethod.Head, "/dual");

        headResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        headResponse.Headers.GetValues("X-Method-Seen").Should().ContainSingle()
            .Which.Should().Be("HEAD",
                "handlers that short-circuit on HEAD (PMTiles range proxy, scene assets) must keep seeing HEAD");
        headResponse.Content.Headers.ContentLength.Should().Be(
            DualEndpointContentLength,
            "a Content-Length the handler set itself must never be replaced by the counted body length");
        (await headResponse.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
    }

    [UnitTest]
    public async Task InvokeAsync_GetOnRouteThatMapsHead_StillStreamsTheBody()
    {
        using var getResponse = await SendAsync(HttpMethod.Get, "/dual");

        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        getResponse.Headers.GetValues("X-Method-Seen").Should().ContainSingle().Which.Should().Be("GET");
        (await getResponse.Content.ReadAsStringAsync()).Should().Be("payload-streamed-for-get");
    }

    [UnitTest]
    public async Task InvokeAsync_HeadOnSeparateExplicitHeadRoute_SelectsHeadHandler()
    {
        using var headResponse = await SendAsync(HttpMethod.Head, "/split");

        headResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        headResponse.Headers.GetValues("X-Handler-Seen").Should().ContainSingle().Which.Should().Be("HEAD");
        headResponse.Content.Headers.ContentLength.Should().Be(DualEndpointContentLength);
        (await headResponse.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
    }

    [UnitTest]
    public async Task InvokeAsync_GetOnSeparateExplicitHeadRoute_SelectsGetHandler()
    {
        using var getResponse = await SendAsync(HttpMethod.Get, "/split");

        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        getResponse.Headers.GetValues("X-Handler-Seen").Should().ContainSingle().Which.Should().Be("GET");
        (await getResponse.Content.ReadAsStringAsync()).Should().Be("get-handler-payload");
    }

    [UnitTest]
    public async Task InvokeAsync_GetOnHeadOnlyRoute_Returns405WithDeclaredAllowHeader()
    {
        using var getResponse = await SendAsync(HttpMethod.Get, "/head-only");

        getResponse.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        getResponse.Content.Headers.Allow.Should().ContainSingle().Which.Should().Be("HEAD");
    }

    [UnitTest]
    public async Task InvokeAsync_HeadOnPostOnlyRoute_Returns405()
    {
        using var response = await SendAsync(HttpMethod.Head, "/post-only");

        response.StatusCode.Should().Be(
            HttpStatusCode.MethodNotAllowed,
            "the rewritten GET does not match a POST-only route either, so the genuine 405 survives");
    }

    [UnitTest]
    public async Task InvokeAsync_HeadOnUnknownRoute_Returns404()
    {
        using var response = await SendAsync(HttpMethod.Head, "/not-mapped");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [UnitTest]
    public async Task InvokeAsync_HeadOnNoContentRoute_DoesNotSynthesizeContentLength()
    {
        using var response = await SendAsync(HttpMethod.Head, "/no-content");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Content.Headers.ContentLength.Should().BeNull(
            "a 204 carries no body, so it must not gain a Content-Length the GET would not have sent");
    }

    [UnitTest]
    public async Task InvokeAsync_HeadOnChunkedRoute_DoesNotSynthesizeContentLength()
    {
        using var response = await SendAsync(HttpMethod.Head, "/chunked");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TransferEncodingChunked.Should().BeTrue();
        response.Content.Headers.ContentLength.Should().BeNull(
            "chunked framing and Content-Length cannot be advertised on the same response");
    }

    [UnitTest]
    public async Task InvokeAsync_NonHeadMethodsOnGetOnlyRoute_StillReturn405()
    {
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch })
        {
            using var response = await SendAsync(method, "/text");

            response.StatusCode.Should().Be(
                HttpStatusCode.MethodNotAllowed,
                "{0} must not be rewritten to GET",
                method.Method);
        }
    }

    [UnitTest]
    public async Task InvokeAsync_HeadOnLongLivedStream_CompletesInsteadOfHanging()
    {
        using var timeout = new CancellationTokenSource(StreamProbeTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Head, "/stream");

        using var response = await _client.SendAsync(request, timeout.Token);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the status must be the one the streaming handler really produced");
        response.Content.Headers.ContentType?.ToString().Should().Be("text/event-stream");
        (await response.Content.ReadAsByteArrayAsync(timeout.Token)).Should().BeEmpty();

        var exited = await Task.WhenAny(
            _streamHandlerExited.Task,
            Task.Delay(StreamProbeTimeout, timeout.Token));
        exited.Should().BeSameAs(
            _streamHandlerExited.Task,
            "the handler's heartbeat loop must be released, not merely detached from the response; " +
            "a stream session held open by a HEAD probe is the resource-exhaustion half of the bug");
    }

    [UnitTest]
    public async Task InvokeAsync_HeadOnLongLivedStream_DoesNotAdvertiseThePreambleAsContentLength()
    {
        using var timeout = new CancellationTokenSource(StreamProbeTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Head, "/stream");

        using var response = await _client.SendAsync(request, timeout.Token);

        response.Content.Headers.ContentLength.Should().BeNull(
            "the equivalent GET is unbounded and sends no Content-Length, so the few preamble " +
            "bytes this probe observed must not be advertised as the response length");
    }

    [UnitTest]
    public async Task InvokeAsync_HeadOnExplicitlyMarkedStreamThatOnlyFlushes_CompletesAndReleasesHandler()
    {
        using var timeout = new CancellationTokenSource(StreamProbeTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Head, "/marked-flush-stream");

        using var response = await _client.SendAsync(request, timeout.Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.ToString().Should().Be("text/event-stream");
        response.Content.Headers.ContentLength.Should().BeNull();

        var exited = await Task.WhenAny(
            _streamHandlerExited.Task,
            Task.Delay(StreamProbeTimeout, timeout.Token));
        exited.Should().BeSameAs(
            _streamHandlerExited.Task,
            "a HEAD probe must release a marked stream even when it flushes headers before writing data");
    }

    [UnitTest]
    public async Task InvokeAsync_HeadOnMarkedStreamThroughResponseCompression_CompletesAndReleasesHandler()
    {
        using var timeout = new CancellationTokenSource(StreamProbeTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Head, "/marked-flush-stream");
        request.Headers.AcceptEncoding.ParseAdd("gzip");

        using var response = await _client.SendAsync(request, timeout.Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.ToString().Should().Be("text/event-stream");
        response.Content.Headers.ContentEncoding.Should().Contain(
            "gzip",
            "the test must exercise the response-compression body-feature wrapper");
        response.Content.Headers.ContentLength.Should().BeNull();

        var exited = await Task.WhenAny(
            _streamHandlerExited.Task,
            Task.Delay(StreamProbeTimeout, timeout.Token));
        exited.Should().BeSameAs(
            _streamHandlerExited.Task,
            "response compression must not hide the HEAD activity callback from a marked stream");
    }

    [UnitTest]
    public async Task InvokeAsync_HeadOnStreamRouteThatRejectsBeforeStreaming_KeepsTheRealStatus()
    {
        using var timeout = new CancellationTokenSource(StreamProbeTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Head, "/stream-missing");

        using var response = await _client.SendAsync(request, timeout.Token);

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "bounding a stream at its first write must not flatten the early returns that precede " +
            "it into a synthetic 200");
    }

    [UnitTest]
    public async Task InvokeAsync_HeadOnHandlerProvidedContentLength_StillDeclaresNoRangeSupport()
    {
        using var response = await SendAsync(HttpMethod.Head, "/fixed-length");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentLength.Should().Be(FixedLengthPayloadSize);
        response.Headers.AcceptRanges.Should().ContainSingle()
            .Which.Should().Be(
                "none",
                "a Content-Length with no Accept-Ranges is exactly the shape GDAL /vsicurl reads as " +
                "'ranges are available'; the handlers that serve a whole buffer set their own length, " +
                "so gating the stamp on a synthesized one left the binary raster responses broken");
    }

    [UnitTest]
    public async Task InvokeAsync_HeadOnRangeServingRoute_PreservesTheHandlersAdvertisement()
    {
        using var response = await SendAsync(HttpMethod.Head, "/ranged");

        response.Headers.AcceptRanges.Should().ContainSingle()
            .Which.Should().Be(
                "bytes",
                "an endpoint that genuinely serves ranges must keep its own advertisement");
    }
}

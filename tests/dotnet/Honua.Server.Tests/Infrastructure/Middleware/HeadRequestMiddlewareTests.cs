// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Infrastructure.Middleware;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    /// <summary>
    /// The request method observed by the stand-in cross-cutting middleware as the pipeline
    /// unwinds. xUnit constructs a fresh instance per test method, so this needs no
    /// cross-test synchronisation.
    /// </summary>
    private string? _upstreamMethodOnUnwind;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddHonuaHeadRequestSupport();

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

        // Registered last, exactly as Program.cs does, so it is the final middleware before the
        // endpoint runs.
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

        _app.MapPost("/post-only", () => Results.Ok(new { ok = true }));
        _app.MapGet("/no-content", () => Results.NoContent());

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
}

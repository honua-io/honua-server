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
/// Pins the one thing the <c>/arcgis</c> alias depends on: that the rewrite runs before
/// endpoint matching. A rewrite registered on the built app runs after the implicit
/// <c>UseRouting</c> of <see cref="WebApplication"/> has already failed to match the
/// aliased path, and the request then finishes with no endpoint - the body is the
/// not-found document even though the status a later logger sees may be 200. That is
/// what happened to the first version of this alias, and it is why the test reads the
/// body rather than the status.
/// </summary>
[Protocol(TestProtocols.Infrastructure)]
public sealed class ArcGisInstancePathAliasTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddHonuaArcGisInstancePathAlias();

        _app = builder.Build();

        _app.MapGet("/rest/info", (HttpContext context) =>
            Results.Text($"info path={context.Request.Path} base={context.Request.PathBase}"));
        _app.MapGet("/rest/services/{service}/GeocodeServer", (string service) =>
            Results.Text($"geocode {service}"));
        _app.MapGet("/", () => Results.Text("root"));

        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Theory]
    [InlineData("/arcgis/rest/info", "info path=/rest/info base=")]
    [InlineData("/ArcGIS/rest/info", "info path=/rest/info base=")]
    [InlineData("/arcgis/rest/services/honua/GeocodeServer", "geocode honua")]
    [InlineData("/arcgis", "root")]
    [InlineData("/arcgis/", "root")]
    public async Task AliasedPath_IsRoutedToTheRootMountedEndpoint(string path, string expectedBody)
    {
        var response = await _client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be(expectedBody,
            "the alias must be applied before endpoint matching, so the endpoint itself answers; " +
            "a post-routing rewrite leaves the request unhandled and its body is the not-found document");
    }

    [Fact]
    public async Task RootPath_IsUnaffected()
    {
        var response = await _client.GetAsync("/rest/info");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("info path=/rest/info base=");
    }

    [Theory]
    [InlineData("/arcgis/rest/nothing-here")]
    [InlineData("/arcgisx/rest/info")]
    public async Task UnservedPath_StaysNotFound(string path)
    {
        var response = await _client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the alias exposes nothing that is not already served at the root, and only the exact /arcgis segment is an alias");
    }
}

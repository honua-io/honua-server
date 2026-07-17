// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Protocols.Stac;

/// <summary>
/// Isolated <see cref="WebAppFixture"/> that opts the hosted Blazor static web assets back
/// on for the STAC ops demo shell tests. The Test host skips ASP.NET's static-web-assets
/// loader by default (honua-server#2904) because it aborts host construction when the
/// sharded Release build has not materialized the Blazor <c>compressed/</c> content root; the
/// few tests that actually assert the served demo shell set
/// <c>HONUA_TEST_HOSTED_BLAZOR_ASSETS=true</c> so the resilient loader runs for this host only.
/// </summary>
public sealed class StacOpsDemoWebAppFixture : IAsyncLifetime
{
    private readonly WebAppFixture _inner = new WebAppFixture()
        .ConfigureWebHost(builder => builder.UseSetting("HONUA_TEST_HOSTED_BLAZOR_ASSETS", "true"));

    /// <summary>Gets the HTTP client bound to the demo-enabled test host.</summary>
    public HttpClient Client => _inner.Client;

    /// <inheritdoc />
    public Task InitializeAsync() => _inner.InitializeAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => _inner.DisposeAsync();
}

/// <summary>
/// Integration tests for the hosted STAC operations demo shell.
/// </summary>
[Protocol(TestProtocols.Stac)]
[Collection("Database")]
public sealed class StacOpsDemoEndpointTests : IClassFixture<StacOpsDemoWebAppFixture>
{
    private readonly StacOpsDemoWebAppFixture _fixture;

    public StacOpsDemoEndpointTests(StacOpsDemoWebAppFixture fixture) => _fixture = fixture;

    [IntegrationTest]
    [Operation(Operations.StacCatalog)]
    [Endpoint("GET /samples/stac-ops")]
    public async Task GetStacOpsDemo_ServesHostedSampleShell()
    {
        using var response = await GetHostedShellAsync("/samples/stac-ops");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");

        var html = await response.Content.ReadAsStringAsync();

        html.Should().Contain("<base href=\"/samples/stac-ops/\" />");
        html.Should().Contain("<link rel=\"stylesheet\" href=\"Honua.StacOpsDemo.styles.css\" />");
        html.Should().Contain("Honua STAC Ops Demo");
    }

    [IntegrationTest]
    [Operation(Operations.StacCatalog)]
    [Endpoint("GET /samples/stac-ops/")]
    public async Task GetStacOpsDemo_WithTrailingSlash_ServesHostedSampleShell()
    {
        using var response = await _fixture.Client.GetAsync("/samples/stac-ops/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");

        var html = await response.Content.ReadAsStringAsync();

        html.Should().Contain("<base href=\"/samples/stac-ops/\" />");
        html.Should().Contain("<link rel=\"stylesheet\" href=\"Honua.StacOpsDemo.styles.css\" />");
        html.Should().Contain("Honua STAC Ops Demo");
    }

    [IntegrationTest]
    [Operation(Operations.StacCatalog)]
    [Endpoint("GET /samples/stac-ops/_framework/blazor.webassembly.js")]
    public async Task GetStacOpsDemoFrameworkAsset_WhenDemoEnabled_ReturnsStaticFile()
    {
        using var response = await _fixture.Client.GetAsync("/samples/stac-ops/_framework/blazor.webassembly.js");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Contain("javascript");
    }

    [IntegrationTest]
    [Operation(Operations.StacCatalog)]
    [Endpoint("GET /samples/stac-ops/index.html")]
    [Endpoint("GET /samples/stac-ops/_framework/blazor.webassembly.js")]
    public async Task GetStacOpsDemoAssets_WhenDemoDisabled_Return404()
    {
        var isolatedFixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("ServeStacOpsDemo", "false");
            });

        try
        {
            await isolatedFixture.InitializeAsync();

            using var shellResponse = await isolatedFixture.Client.GetAsync("/samples/stac-ops/index.html");
            using var frameworkResponse = await isolatedFixture.Client.GetAsync("/samples/stac-ops/_framework/blazor.webassembly.js");

            shellResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
            frameworkResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            await isolatedFixture.DisposeAsync();
        }
    }

    private async Task<HttpResponseMessage> GetHostedShellAsync(string path)
    {
        var initialResponse = await _fixture.Client.GetAsync(path);
        if (initialResponse.StatusCode == HttpStatusCode.OK)
        {
            return initialResponse;
        }

        initialResponse.StatusCode.Should().Be(HttpStatusCode.Found);
        initialResponse.Headers.Location.Should().NotBeNull();
        initialResponse.Headers.Location!.ToString().Should().Be("/samples/stac-ops/index.html");
        initialResponse.Dispose();

        return await FollowRedirectsAsync("/samples/stac-ops/index.html");
    }

    private async Task<HttpResponseMessage> FollowRedirectsAsync(string path)
    {
        var currentPath = path;
        for (var redirectCount = 0; redirectCount < 4; redirectCount++)
        {
            var response = await _fixture.Client.GetAsync(currentPath);
            if (response.StatusCode is not HttpStatusCode.Found and
                not HttpStatusCode.MovedPermanently and
                not HttpStatusCode.RedirectKeepVerb and
                not HttpStatusCode.TemporaryRedirect and
                not HttpStatusCode.PermanentRedirect)
            {
                return response;
            }

            response.Headers.Location.Should().NotBeNull();
            currentPath = response.Headers.Location!.ToString();
            response.Dispose();
        }

        throw new InvalidOperationException("Hosted STAC ops demo kept redirecting.");
    }
}

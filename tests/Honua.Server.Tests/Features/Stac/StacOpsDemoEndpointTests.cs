// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Stac;

/// <summary>
/// Integration tests for the hosted STAC operations demo shell.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Stac)]
public sealed class StacOpsDemoEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

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

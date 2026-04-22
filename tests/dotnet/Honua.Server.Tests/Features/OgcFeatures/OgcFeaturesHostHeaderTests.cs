// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.OgcFeatures;

/// <summary>
/// Verifies OGC Features metadata links are not derived from untrusted Host headers.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.OgcApiFeatures)]
public sealed class OgcFeaturesHostHeaderTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features")]
    public async Task LandingPage_WithForgedHostHeader_DoesNotReflectAttackerHostInLinks()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ogc/features?f=json");
        request.Headers.Host = "attacker.example";

        using var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);
        var hrefs = json.RootElement
            .GetProperty("links")
            .EnumerateArray()
            .Select(link => link.GetProperty("href").GetString())
            .Where(href => !string.IsNullOrWhiteSpace(href))
            .ToArray();

        hrefs.Should().NotBeEmpty();
        hrefs.Should().OnlyContain(href =>
            href!.StartsWith('/') ||
            Uri.IsWellFormedUriString(href, UriKind.Absolute));
        hrefs.Should().NotContain(href => href!.Contains("attacker.example", StringComparison.OrdinalIgnoreCase));
    }
}

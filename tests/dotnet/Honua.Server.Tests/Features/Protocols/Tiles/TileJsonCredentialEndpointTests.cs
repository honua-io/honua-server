// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;

namespace Honua.Server.Tests.Features.Protocols.Tiles;

[Collection("Database")]
[Protocol(TestProtocols.Mvt)]
public sealed class TileJsonCredentialEndpointTests : IAsyncLifetime
{
    private const string Referer = "https://maps.example.test/";
    private readonly WebAppFixture _fixture = new WebAppFixture().ConfigureWebHost(builder =>
    {
        builder.UseEnvironment("Test");
        builder.UseSetting("HONUA_DEV_AUTH", "false");
        builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        builder.UseSetting("Authentication:PortalToken:RequireHttps", "false");
    });

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        // Layer 0's first publication is the seeded feature dataset, not the separate
        // ImageServer publication that shares its storage index.
        _fixture.UpdateV2ResourceMetadata(WebAppFixture.TestLayerId, accessPolicy: new AccessPolicy());
        var resource = _fixture.GetCurrentV2GraphSnapshot().Graph.Resources.Single(r => r.Metadata.Id == "res-layer-0");
        resource.AccessPolicy.Should().Be(new AccessPolicy());
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("default")]
    [InlineData("dark")]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /tiles/{layerId}/tile.json")]
    [Endpoint("GET /api/styles/{layerId}.json")]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task ProtectedTileJson_AdvertisedStyleAndTilesKeepBoundToken(string theme)
    {
        using var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Referrer = new Uri(Referer);
        const string metadataPath = "/tiles/0/tile.json";
        using var denied = await client.GetAsync(metadataPath);
        denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Two callers must receive their own links rather than a credential-bearing
        // response stored by the preceding request under the named metadata policy.
        var firstToken = await IssueTokenAsync(client);
        var secondToken = await IssueTokenAsync(client);
        secondToken.Should().NotBe(firstToken);
        // The existing API-key-as-token contract uses the same URL carrier.
        foreach (var token in new[] { firstToken, secondToken, WebAppFixture.SharedAdminPassword })
        {
            using var metadata = await ReadJsonAsync(client, metadataPath + "?token=" + Uri.EscapeDataString(token));
            var tileUrl = metadata.RootElement.GetProperty("tiles")[0].GetString()!;
            var styleUrl = metadata.RootElement.GetProperty("style").GetString()!;
            AssertToken(tileUrl, token);
            AssertToken(styleUrl, token);
            using var style = await ReadJsonAsync(client, ToRequestPath(styleUrl) + "&theme=" + theme);
            var styleTileUrl = style.RootElement.GetProperty("sources").GetProperty("layer-0").GetProperty("tiles")[0].GetString()!;
            AssertToken(styleTileUrl, token);

            foreach (var template in new[] { tileUrl, styleTileUrl })
            {
                var path = ToRequestPath(template).Replace("{z}", "0", StringComparison.Ordinal)
                    .Replace("{x}", "0", StringComparison.Ordinal).Replace("{y}", "0", StringComparison.Ordinal);
                // Uri escapes template braces when extracting PathAndQuery.
                path = path.Replace("%7Bz%7D", "0", StringComparison.OrdinalIgnoreCase)
                    .Replace("%7Bx%7D", "0", StringComparison.OrdinalIgnoreCase).Replace("%7By%7D", "0", StringComparison.OrdinalIgnoreCase);
                using var tile = await client.GetAsync(path);
                tile.StatusCode.Should().Be(HttpStatusCode.OK);
                tile.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.mapbox-vector-tile");
                (await tile.Content.ReadAsByteArrayAsync()).Should().NotBeEmpty();
            }
        }

        // The propagated credential retains its original Referer policy.
        client.DefaultRequestHeaders.Referrer = new Uri("https://other.example.test/");
        using var wrongBinding = await client.GetAsync(metadataPath + "?token=" + Uri.EscapeDataString(firstToken));
        wrongBinding.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var anonymousStyle = await client.GetAsync("/api/styles/0.json");
        anonymousStyle.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var anonymousTile = await client.GetAsync("/tiles/0/0/0/0.mvt");
        anonymousTile.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var conflictingTokens = await client.GetAsync(metadataPath + "?token=" + Uri.EscapeDataString(firstToken) + "&token=" + Uri.EscapeDataString(secondToken));
        conflictingTokens.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /api/styles/{layerId}.json")]
    public async Task ProtectedStyle_OnlyOwnedTileSourcesInheritToken()
    {
        const string storedStyle = """
            {"version":8,"sprite":"/sprites/default","glyphs":"/fonts/{fontstack}/{range}.pbf",
             "sources":{
               "owned":{"type":"vector","tiles":["/tiles/0/{z}/{x}/{y}.mvt?variant=one"]},
               "tilejson":{"type":"vector","url":"/tiles/0/tile.json"},
               "external":{"type":"vector","tiles":["https://external.example.test/tiles/0/{z}/{x}/{y}.mvt"]},
               "scheme":{"type":"vector","tiles":["//external.example.test/tiles/0/{z}/{x}/{y}.mvt"]},
               "redirect":{"type":"vector","tiles":["/redirect?target=https://external.example.test"]},
               "bound":{"type":"vector","tiles":["/tiles/0/{z}/{x}/{y}.mvt?token=source-owned"]}
             },"layers":[]}
            """;
        var catalog = _fixture.GetService<ILayerStyleCatalog>();
        (await catalog.SetMapLibreStyleAsync(0, storedStyle)).Should().NotBeNull();
        using var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Referrer = new Uri(Referer);
        var token = await IssueTokenAsync(client);
        using var response = await ReadJsonAsync(client, "/api/styles/0.json?token=" + Uri.EscapeDataString(token));
        var root = response.RootElement;
        var sources = root.GetProperty("sources");
        AssertToken(sources.GetProperty("owned").GetProperty("tiles")[0].GetString()!, token);
        AssertToken(sources.GetProperty("tilejson").GetProperty("url").GetString()!, token);
        sources.GetProperty("owned").GetProperty("tiles")[0].GetString().Should().Contain("?variant=one&token=");
        sources.GetProperty("external").GetProperty("tiles")[0].GetString().Should().Be("https://external.example.test/tiles/0/{z}/{x}/{y}.mvt");
        sources.GetProperty("scheme").GetProperty("tiles")[0].GetString().Should().Be("//external.example.test/tiles/0/{z}/{x}/{y}.mvt");
        sources.GetProperty("redirect").GetProperty("tiles")[0].GetString().Should().NotContain("token=");
        AssertToken(sources.GetProperty("bound").GetProperty("tiles")[0].GetString()!, "source-owned");
        root.GetProperty("sprite").GetString().Should().NotContain("token=");
        root.GetProperty("glyphs").GetString().Should().NotContain("token=");

        // Projection must not persist this caller's token into the canonical style.
        var stored = await catalog.GetLayerStyleAsync(0);
        stored.Should().NotBeNull();
        stored!.MapLibreStyleJson.Should().NotContain(token);
    }

    private static void AssertToken(string url, string expected)
    {
        var uri = new Uri(url);
        var values = QueryHelpers.ParseQuery(uri.Query)["token"];
        values.Count.Should().Be(1);
        values[0].Should().Be(expected);
    }

    private static string ToRequestPath(string url) => new Uri(url).PathAndQuery;

    private static async Task<JsonDocument> ReadJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> IssueTokenAsync(HttpClient client)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "admin",
            ["password"] = WebAppFixture.SharedAdminPassword,
            ["client"] = "referer",
            ["referer"] = Referer,
            ["f"] = "json"
        });
        using var response = await client.PostAsync("/sharing/rest/generateToken", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.TryGetProperty("error", out _).Should().BeFalse();
        return json.RootElement.GetProperty("token").GetString()!;
    }
}

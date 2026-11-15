// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.Stac;

[Collection("Database")]
[Protocol(TestProtocols.Stac)]
public sealed class StacMetadataTextTests
{
    private const string UnicodeText = "Rivers & <lakes> \"survey\" '2026' 🌊\t\r\n";
    private const string ControlText = UnicodeText + "before\u0000\u0001after\uFFFE";

    [Theory]
    [Trait("Tier", "Integration")]
    [InlineData(UnicodeText)]
    [InlineData(ControlText)]
    [Operation(Operations.Query)]
    [Endpoint("GET /stac/collections")]
    public Task GetCollections_UnusualMetadataText_RoundTripsJson(string text)
        => AssertMetadataRoundTripAsync("/stac/collections", text);

    [Theory]
    [Trait("Tier", "Integration")]
    [InlineData(UnicodeText)]
    [InlineData(ControlText)]
    [Operation(Operations.GetById)]
    [Endpoint("GET /stac/collections/{collectionId}")]
    public Task GetCollection_UnusualMetadataText_RoundTripsJson(string text)
        => AssertMetadataRoundTripAsync($"/stac/collections/{WebAppFixture.TestLayerId}", text);

    private static void SetMetadata(WebAppFixture fixture, string title, string description)
    {
        fixture.MutateV2ResourceObjectMetadata(WebAppFixture.TestLayerId,
            metadata => metadata with { Title = title, Description = description });
        var snapshot = fixture.GetCurrentV2GraphSnapshot();
        fixture.GetService<TestMetadataV2GraphProvider>().SetGraph(snapshot.Graph with
        {
            Publications = snapshot.Graph.Publications.Select(publication =>
                publication.LayerIndex == WebAppFixture.TestLayerId
                    ? publication with { Metadata = publication.Metadata with { Title = title } }
                    : publication).ToArray(),
            Revision = snapshot.Graph.Revision + 1
        }, schema: fixture.CurrentSchema);
    }

    private static async Task AssertMetadataRoundTripAsync(string path, string text)
    {
        await using var fixture = new WebAppFixture();
        await fixture.InitializeAsync();
        var title = "Title " + text;
        var description = "Description " + text;
        SetMetadata(fixture, title, description);

        using var response = await fixture.Client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var collection = json.RootElement;
        if (collection.TryGetProperty("collections", out var collections))
        {
            var collectionId = WebAppFixture.TestLayerId.ToString(CultureInfo.InvariantCulture);
            collection = collections.EnumerateArray()
                .Single(item => item.GetProperty("id").GetString() == collectionId);
        }

        collection.GetProperty("title").GetString().Should().Be(title);
        collection.GetProperty("description").GetString().Should().Be(description);
    }
}

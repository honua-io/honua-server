// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.OgcFeatures;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.OgcFeatures;

[Collection("Database")]
[Protocol(Protocols.OgcApiFeatures)]
[Operation(Operations.GetMetadata)]
public sealed class OgcFeaturesCollectionMetadataTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}")]
    public async Task GetCollection_WithTemporalFields_AdvertisesTemporalExtent()
    {
        var response = await _fixture.Client.GetAsync("/ogc/features/collections/0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var collection = JsonSerializer.Deserialize(content, OgcJsonContext.Default.CollectionInfo);

        collection.Should().NotBeNull();
        collection!.Extent.Should().NotBeNull();
        collection.Extent!.Temporal.Should().NotBeNull();
        collection.Extent.Temporal!.Interval.Should().NotBeEmpty();
        collection.Extent.Temporal.Interval[0].Length.Should().Be(2);
        collection.Extent.Temporal.Interval[0][0].Should().BeNull();
        collection.Extent.Temporal.Interval[0][1].Should().BeNull();
    }
}

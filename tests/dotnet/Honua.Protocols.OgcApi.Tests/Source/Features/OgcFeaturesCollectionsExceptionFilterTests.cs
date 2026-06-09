// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

[Collection("Database")]
[Protocol(TestProtocols.OgcApiFeatures)]
public sealed class OgcFeaturesCollectionsExceptionFilterTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture;

    public OgcFeaturesCollectionsExceptionFilterTests()
    {
        _fixture = new WebAppFixture()
            .ReplaceService<IMetadataV2GraphProvider>(
                new ThrowingMetadataV2GraphProvider(() => new ArgumentException("Invalid Parse failure")));
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}")]
    public async Task GetCollection_WhenMetadataGraphThrowsArgumentException_Returns500()
    {
        var response = await _fixture.Client.GetAsync("/ogc/features/collections/0");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().NotContain("Invalid Parse failure");
    }

    [Fact]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/queryables")]
    public async Task GetQueryables_WhenMetadataGraphThrowsArgumentException_Returns500()
    {
        var response = await _fixture.Client.GetAsync("/ogc/features/collections/0/queryables");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().NotContain("Invalid Parse failure");
    }

    private sealed class ThrowingMetadataV2GraphProvider(Func<Exception> exceptionFactory) : IMetadataV2GraphProvider
    {
        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => new(Task.FromException<MetadataV2GraphSnapshot>(exceptionFactory()));

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => new(Task.FromException<MetadataV2GraphSnapshot?>(exceptionFactory()));
    }
}

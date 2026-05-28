// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

[Collection("Database")]
[Protocol(TestProtocols.OgcApiFeatures)]
public sealed class OgcFeaturesCollectionsExceptionMappingTests
{
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}")]
    public async Task GetCollection_WhenMetadataGraphThrowsUnexpectedInvalidOperation_ReturnsInternalServerError()
    {
        var fixture = CreateFixtureThatThrows(() => new InvalidOperationException("catalog backend failure"));
        await fixture.InitializeAsync();

        try
        {
            var response = await fixture.Client.GetAsync("/ogc/features/collections/0");

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            var payload = await response.Content.ReadAsStringAsync();
            payload.Should().NotContain("catalog backend failure");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}")]
    public async Task GetCollection_WhenMetadataGraphThrowsResourceNotFound_ReturnsNotFound()
    {
        var fixture = CreateFixtureThatThrows(() => new ResourceNotFoundException("Layer not found."));
        await fixture.InitializeAsync();

        try
        {
            var response = await fixture.Client.GetAsync("/ogc/features/collections/0");

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/queryables")]
    public async Task GetQueryables_WhenMetadataGraphThrowsUnexpectedInvalidOperation_ReturnsInternalServerError()
    {
        var fixture = CreateFixtureThatThrows(() => new InvalidOperationException("catalog backend failure"));
        await fixture.InitializeAsync();

        try
        {
            var response = await fixture.Client.GetAsync("/ogc/features/collections/0/queryables");

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            var payload = await response.Content.ReadAsStringAsync();
            payload.Should().NotContain("catalog backend failure");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/queryables")]
    public async Task GetQueryables_WhenMetadataGraphThrowsResourceNotFound_ReturnsNotFound()
    {
        var fixture = CreateFixtureThatThrows(() => new ResourceNotFoundException("Layer not found."));
        await fixture.InitializeAsync();

        try
        {
            var response = await fixture.Client.GetAsync("/ogc/features/collections/0/queryables");

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static WebAppFixture CreateFixtureThatThrows(Func<Exception> exceptionFactory)
    {
        return new WebAppFixture()
            .ReplaceService<IMetadataV2GraphProvider>(
                new ThrowingMetadataV2GraphProvider(exceptionFactory));
    }

    private sealed class ThrowingMetadataV2GraphProvider(Func<Exception> exceptionFactory) : IMetadataV2GraphProvider
    {
        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => new(Task.FromException<MetadataV2GraphSnapshot>(exceptionFactory()));

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => new(Task.FromException<MetadataV2GraphSnapshot?>(exceptionFactory()));
    }
}

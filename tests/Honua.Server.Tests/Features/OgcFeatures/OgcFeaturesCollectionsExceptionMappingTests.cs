// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.OgcFeatures;

[Collection("Database")]
[Protocol(Protocols.OgcApiFeatures)]
public sealed class OgcFeaturesCollectionsExceptionMappingTests
{
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}")]
    public async Task GetCollection_WhenCatalogThrowsUnexpectedInvalidOperation_ReturnsInternalServerError()
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
    public async Task GetCollection_WhenCatalogThrowsResourceNotFound_ReturnsNotFound()
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
    public async Task GetQueryables_WhenCatalogThrowsUnexpectedInvalidOperation_ReturnsInternalServerError()
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
    public async Task GetQueryables_WhenCatalogThrowsResourceNotFound_ReturnsNotFound()
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
        var catalog = Substitute.For<ILayerCatalog>();
        catalog.GetLayerAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<LayerDefinition?>(exceptionFactory()));
        catalog.ListLayersAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Array.Empty<LayerDefinition>()));
        catalog.GetServiceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ServiceDefinition?>(null));
        catalog.ListServicesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Array.Empty<ServiceDefinition>()));
        catalog.LayerExistsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));
        catalog.ServiceExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));
        catalog.GetRelationshipAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Relationship?>(null));
        catalog.ListRelationshipsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Array.Empty<Relationship>()));

        return new WebAppFixture()
            .ReplaceService<ILayerCatalog>(catalog);
    }
}

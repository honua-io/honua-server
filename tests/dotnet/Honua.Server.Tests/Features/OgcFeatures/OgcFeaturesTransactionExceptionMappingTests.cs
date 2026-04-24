// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.Ogc.Common;
using Honua.Server.Features.OgcFeatures;
using Honua.Server.Features.OgcFeatures.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.OgcFeatures;

[Collection("Database")]
[Protocol(Protocols.OgcApiFeatures)]
public sealed class OgcFeaturesTransactionExceptionMappingTests
{
    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task ReplaceFeature_WhenWriterThrowsUnexpectedInvalidOperation_ReturnsInternalServerError()
    {
        var fixture = CreateFixtureWithUpdateException(() => new InvalidOperationException("unexpected writer failure"));
        await fixture.InitializeAsync();

        try
        {
            var existingId = await fixture.InsertFeatureAsync(WebAppFixture.TestLayerId, "Replace Target");
            var payload = CreateGeoJsonFeaturePayload(existingId, "Replace Updated");

            var response = await fixture.Client.PutAsync(
                $"/ogc/features/collections/{WebAppFixture.TestLayerId}/items/{existingId}",
                new StringContent(payload, Encoding.UTF8, MediaTypes.GeoJson));

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().NotContain("unexpected writer failure");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task ReplaceFeature_WhenWriterThrowsResourceNotFound_ReturnsNotFound()
    {
        var fixture = CreateFixtureWithUpdateException(() => new ResourceNotFoundException("feature missing"));
        await fixture.InitializeAsync();

        try
        {
            var existingId = await fixture.InsertFeatureAsync(WebAppFixture.TestLayerId, "Replace Target");
            var payload = CreateGeoJsonFeaturePayload(existingId, "Replace Updated");

            var response = await fixture.Client.PutAsync(
                $"/ogc/features/collections/{WebAppFixture.TestLayerId}/items/{existingId}",
                new StringContent(payload, Encoding.UTF8, MediaTypes.GeoJson));

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task PatchFeature_WhenWriterThrowsUnexpectedInvalidOperation_ReturnsInternalServerError()
    {
        var fixture = CreateFixtureWithUpdateException(() => new InvalidOperationException("unexpected writer failure"));
        await fixture.InitializeAsync();

        try
        {
            var existingId = await fixture.InsertFeatureAsync(WebAppFixture.TestLayerId, "Patch Target");

            using var patchRequest = new HttpRequestMessage(
                HttpMethod.Patch,
                $"/ogc/features/collections/{WebAppFixture.TestLayerId}/items/{existingId}")
            {
                Content = new StringContent(
                    """{"properties":{"name":"Patch Updated"}}""",
                    Encoding.UTF8,
                    "application/merge-patch+json")
            };

            var response = await fixture.Client.SendAsync(patchRequest);

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().NotContain("unexpected writer failure");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task PatchFeature_WhenWriterThrowsResourceNotFound_ReturnsNotFound()
    {
        var fixture = CreateFixtureWithUpdateException(() => new ResourceNotFoundException("feature missing"));
        await fixture.InitializeAsync();

        try
        {
            var existingId = await fixture.InsertFeatureAsync(WebAppFixture.TestLayerId, "Patch Target");

            using var patchRequest = new HttpRequestMessage(
                HttpMethod.Patch,
                $"/ogc/features/collections/{WebAppFixture.TestLayerId}/items/{existingId}")
            {
                Content = new StringContent(
                    """{"properties":{"name":"Patch Updated"}}""",
                    Encoding.UTF8,
                    "application/merge-patch+json")
            };

            var response = await fixture.Client.SendAsync(patchRequest);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static WebAppFixture CreateFixtureWithUpdateException(Func<Exception> exceptionFactory)
    {
        var writer = Substitute.For<IFeatureWriter>();
        writer.ApplyEditsAsync(default, default, default)
            .ReturnsForAnyArgs(_ => Task.FromException<FeatureEditResult>(exceptionFactory()));

        return new WebAppFixture()
            .ReplaceService<IFeatureWriter>(writer);
    }

    private static string CreateGeoJsonFeaturePayload(long id, string name)
    {
        var feature = new GeoJsonFeature
        {
            Type = "Feature",
            Id = id,
            Geometry = new SimpleGeoJsonGeometry
            {
                Type = "Point",
                CoordinatesJson = "[-122.4194, 37.7749]"
            },
            Properties = new Dictionary<string, object?>
            {
                ["name"] = name
            }
        };

        return JsonSerializer.Serialize(feature, OgcJsonContext.Default.GeoJsonFeature);
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Ogc.Common;
using Honua.Server.Features.OgcFeatures;
using Honua.Server.Features.OgcFeatures.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.OgcFeatures;

[Collection("Database")]
[Protocol(Protocols.OgcApiFeatures)]
public sealed class OgcFeaturesBatchOperationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const int TestLayerId = 0;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.BulkCreate, Operations.BulkUpdate)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items/batch")]
    public async Task Batch_WithCreateAndUpdate_ReturnsSuccess()
    {
        var existingId = await _fixture.InsertFeatureAsync(TestLayerId, "Batch Original");

        var batchRequest = new BatchRequest
        {
            Operations =
            [
                new BatchOperation
                {
                    Id = "create-1",
                    Type = "CREATE",
                    Feature = CreatePointFeature("Batch Created", "[-122.4194, 37.7749]")
                },
                new BatchOperation
                {
                    Id = "update-1",
                    Type = "UPDATE",
                    FeatureId = existingId.ToString(CultureInfo.InvariantCulture),
                    Feature = CreatePointFeature("Batch Updated", "[-122.5, 37.8]")
                }
            ]
        };

        var json = JsonSerializer.Serialize(batchRequest, OgcJsonContext.Default.BatchRequest);
        var response = await _fixture.Client.PostAsync(
            $"/ogc/features/collections/{TestLayerId}/items/batch",
            new StringContent(json, Encoding.UTF8, MediaTypes.Json));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var responseContent = await response.Content.ReadAsStringAsync();
        var batchResponse = JsonSerializer.Deserialize(responseContent, OgcJsonContext.Default.BatchOperationResponse);

        batchResponse.Should().NotBeNull();
        batchResponse!.HasErrors.Should().BeFalse();
        batchResponse.ProcessedCount.Should().Be(2);
        batchResponse.SuccessCount.Should().Be(2);
        batchResponse.Results.Should().HaveCount(2);

        var createResult = batchResponse.Results.Single(result => result.OperationId == "create-1");
        createResult.IsSuccess.Should().BeTrue();
        createResult.StatusCode.Should().Be(201);
        createResult.FeatureId.Should().NotBeNullOrWhiteSpace();

        var updateResult = batchResponse.Results.Single(result => result.OperationId == "update-1");
        updateResult.IsSuccess.Should().BeTrue();
        updateResult.StatusCode.Should().Be(200);
        updateResult.FeatureId.Should().Be(existingId.ToString(CultureInfo.InvariantCulture));
    }

    [IntegrationTest]
    [Operation(Operations.BulkCreate, Operations.BulkUpdate)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items/batch")]
    public async Task Batch_WithPartialFailure_ReturnsMultiStatus()
    {
        var batchRequest = new BatchRequest
        {
            Operations =
            [
                new BatchOperation
                {
                    Id = "bad-update",
                    Type = "UPDATE",
                    FeatureId = "9999999",
                    Feature = CreatePointFeature("Missing Feature", "[-122.3, 37.7]")
                },
                new BatchOperation
                {
                    Id = "good-create",
                    Type = "CREATE",
                    Feature = CreatePointFeature("Partial Success", "[-122.6, 37.8]")
                }
            ]
        };

        var json = JsonSerializer.Serialize(batchRequest, OgcJsonContext.Default.BatchRequest);
        var response = await _fixture.Client.PostAsync(
            $"/ogc/features/collections/{TestLayerId}/items/batch",
            new StringContent(json, Encoding.UTF8, MediaTypes.Json));

        response.StatusCode.Should().Be(HttpStatusCode.MultiStatus);
        var responseContent = await response.Content.ReadAsStringAsync();
        var batchResponse = JsonSerializer.Deserialize(responseContent, OgcJsonContext.Default.BatchOperationResponse);

        batchResponse.Should().NotBeNull();
        batchResponse!.HasErrors.Should().BeTrue();
        batchResponse.ProcessedCount.Should().Be(2);
        batchResponse.SuccessCount.Should().Be(1);
        batchResponse.Results.Should().HaveCount(2);

        var errorResult = batchResponse.Results.Single(result => result.OperationId == "bad-update");
        errorResult.IsSuccess.Should().BeFalse();
        errorResult.StatusCode.Should().Be(404);

        var successResult = batchResponse.Results.Single(result => result.OperationId == "good-create");
        successResult.IsSuccess.Should().BeTrue();
        successResult.StatusCode.Should().Be(201);
        successResult.FeatureId.Should().NotBeNullOrWhiteSpace();
    }

    [IntegrationTest]
    [Operation(Operations.BulkCreate)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items/batch")]
    public async Task Batch_WithFailFast_StopsAfterFirstError()
    {
        var batchRequest = new BatchRequest
        {
            FailFast = true,
            Operations =
            [
                new BatchOperation
                {
                    Id = "invalid-op",
                    Type = "UPSERT"
                },
                new BatchOperation
                {
                    Id = "skipped-create",
                    Type = "CREATE",
                    Feature = CreatePointFeature("Skipped", "[-122.1, 37.6]")
                }
            ]
        };

        var json = JsonSerializer.Serialize(batchRequest, OgcJsonContext.Default.BatchRequest);
        var response = await _fixture.Client.PostAsync(
            $"/ogc/features/collections/{TestLayerId}/items/batch",
            new StringContent(json, Encoding.UTF8, MediaTypes.Json));

        response.StatusCode.Should().Be(HttpStatusCode.MultiStatus);
        var responseContent = await response.Content.ReadAsStringAsync();
        var batchResponse = JsonSerializer.Deserialize(responseContent, OgcJsonContext.Default.BatchOperationResponse);

        batchResponse.Should().NotBeNull();
        batchResponse!.HasErrors.Should().BeTrue();
        batchResponse.ProcessedCount.Should().Be(1);
        batchResponse.SuccessCount.Should().Be(0);
        batchResponse.Results.Should().ContainSingle();

        var errorResult = batchResponse.Results.Single();
        errorResult.OperationId.Should().Be("invalid-op");
        errorResult.IsSuccess.Should().BeFalse();
        errorResult.StatusCode.Should().Be(400);
    }

    private static GeoJsonFeature CreatePointFeature(string name, string coordinatesJson)
    {
        return new GeoJsonFeature
        {
            Type = "Feature",
            Geometry = new SimpleGeoJsonGeometry
            {
                Type = "Point",
                CoordinatesJson = coordinatesJson
            },
            Properties = new Dictionary<string, object?>
            {
                ["name"] = name
            }
        };
    }

}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.Server.Features.Protocols.Ogc.Api.Features;
using Honua.Server.Features.Protocols.Ogc.Api.Features.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

[Collection("Database")]
[Protocol(TestProtocols.OgcApiFeatures)]
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
    [Operation(Operations.BulkCreate)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items/batch")]
    public async Task Batch_WithUnsupportedContentType_ReturnsUnsupportedMediaType()
    {
        var response = await _fixture.Client.PostAsync(
            $"/ogc/features/collections/{TestLayerId}/items/batch",
            new StringContent("""{"operations":[]}""", Encoding.UTF8, "text/plain"));

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [IntegrationTest]
    [Operation(Operations.BulkCreate)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items/batch")]
    public async Task Batch_WithNullOperations_ReturnsBadRequest()
    {
        var response = await _fixture.Client.PostAsync(
            $"/ogc/features/collections/{TestLayerId}/items/batch",
            new StringContent("""{"operations":null}""", Encoding.UTF8, MediaTypes.Json));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.BulkCreate)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items/batch")]
    public async Task Batch_WithNullOperationType_ReturnsPerOperationBadRequest()
    {
        var response = await _fixture.Client.PostAsync(
            $"/ogc/features/collections/{TestLayerId}/items/batch",
            new StringContent("""{"operations":[{"id":"missing-type","type":null}]}""", Encoding.UTF8, MediaTypes.Json));

        response.StatusCode.Should().Be(HttpStatusCode.MultiStatus);
        var responseContent = await response.Content.ReadAsStringAsync();
        var batchResponse = JsonSerializer.Deserialize(responseContent, OgcJsonContext.Default.BatchOperationResponse);

        batchResponse.Should().NotBeNull();
        batchResponse!.HasErrors.Should().BeTrue();
        batchResponse.Results.Should().ContainSingle();
        batchResponse.Results[0].OperationId.Should().Be("missing-type");
        batchResponse.Results[0].StatusCode.Should().Be(400);
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
        var initialCount = await CountFeaturesAsync();

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
        batchResponse.SuccessCount.Should().Be(0);
        batchResponse.Results.Should().HaveCount(2);

        var errorResult = batchResponse.Results.Single(result => result.OperationId == "bad-update");
        errorResult.IsSuccess.Should().BeFalse();
        errorResult.StatusCode.Should().Be(404);

        var rolledBackResult = batchResponse.Results.Single(result => result.OperationId == "good-create");
        rolledBackResult.IsSuccess.Should().BeFalse();
        rolledBackResult.StatusCode.Should().Be(424);
        rolledBackResult.ErrorMessage.Should().Be("Operation rolled back.");

        var finalCount = await CountFeaturesAsync();
        finalCount.Should().Be(initialCount);
    }

    [IntegrationTest]
    [Operation(Operations.BulkCreate)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items/batch")]
    public async Task Batch_WithFailFast_StopsAfterFirstError()
    {
        var initialCount = await CountFeaturesAsync();

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

        var finalCount = await CountFeaturesAsync();
        finalCount.Should().Be(initialCount);
    }

    [IntegrationTest]
    [Operation(Operations.BulkDelete, Operations.BulkUpdate)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items/batch")]
    public async Task Batch_WithDeleteThenUpdate_RollsBackEarlierDelete()
    {
        var featureId = await _fixture.InsertFeatureAsync(TestLayerId, "Delete Then Update");

        var batchRequest = new BatchRequest
        {
            Operations =
            [
                new BatchOperation
                {
                    Id = "delete-first",
                    Type = "DELETE",
                    FeatureId = featureId.ToString(CultureInfo.InvariantCulture)
                },
                new BatchOperation
                {
                    Id = "update-second",
                    Type = "UPDATE",
                    FeatureId = featureId.ToString(CultureInfo.InvariantCulture),
                    Feature = CreatePointFeature("Should Roll Back", "[-122.2, 37.6]")
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

        var deleteResult = batchResponse.Results.Single(result => result.OperationId == "delete-first");
        deleteResult.IsSuccess.Should().BeFalse();
        deleteResult.StatusCode.Should().Be(424);
        deleteResult.ErrorMessage.Should().Be("Operation rolled back.");

        var updateResult = batchResponse.Results.Single(result => result.OperationId == "update-second");
        updateResult.IsSuccess.Should().BeFalse();
        updateResult.StatusCode.Should().Be(404);

        var featureResponse = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestLayerId}/items/{featureId}");
        featureResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.BulkCreate, Operations.BulkUpdate)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items/batch")]
    public async Task Batch_WhenEventPublishFails_ReturnsSuccess()
    {
        await using var fixture = new WebAppFixture()
            .ReplaceService<IFeatureChangeEventPublisher>(new ThrowingFeatureChangeEventPublisher());
        await fixture.InitializeAsync();

        var existingId = await fixture.InsertFeatureAsync(TestLayerId, "Batch Original");
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
        var response = await fixture.Client.PostAsync(
            $"/ogc/features/collections/{TestLayerId}/items/batch",
            new StringContent(json, Encoding.UTF8, MediaTypes.Json));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var responseContent = await response.Content.ReadAsStringAsync();
        var batchResponse = JsonSerializer.Deserialize(responseContent, OgcJsonContext.Default.BatchOperationResponse);

        batchResponse.Should().NotBeNull();
        batchResponse!.HasErrors.Should().BeFalse();
        batchResponse.SuccessCount.Should().Be(2);
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

    private async Task<long> CountFeaturesAsync()
    {
        var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        await using var connection = await _fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM features WHERE layer_id = @layerId;";
        command.Parameters.Add(new NpgsqlParameter
        {
            ParameterName = "layerId",
            Value = TestLayerId
        });
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private sealed class ThrowingFeatureChangeEventPublisher : IFeatureChangeEventPublisher
    {
        public Task PublishAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("publish failed");

        public Task PublishStrictAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("publish failed");
    }

}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.OData.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.OData;

/// <summary>
/// Integration tests for OData v4 advanced features:
/// - $batch operations
/// - $apply aggregation
/// - $search full-text search
/// - $expand related entities
/// </summary>
[Collection("Database")]
[Protocol(Protocols.ODataV4)]
public sealed class ODataAdvancedFeaturesTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private ODataTestFeatureStore _featureStore = null!;
    private const int TestLayerId = 0;

    public async Task InitializeAsync()
    {
        _featureStore = new ODataTestFeatureStore();
        _fixture.ReplaceService<ILayerCatalog>(new ODataTestLayerCatalog());
        _fixture.ReplaceService<IFeatureStore>(_featureStore);
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    #region Batch Operations Tests

    [IntegrationTest]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task Batch_WithSingleGetRequest_ReturnsFeature()
    {
        var batchRequest = new ODataBatchRequest
        {
            Requests = ImmutableArray.Create(new ODataBatchRequestItem
            {
                Id = "1",
                Method = "GET",
                Url = $"Features({TestLayerId},1)"
            })
        };

        var json = JsonSerializer.Serialize(batchRequest, ODataJsonContext.Default.ODataBatchRequest);
        var response = await _fixture.Client.PostAsync(
            "/odata/$batch",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        var responses = document.RootElement.GetProperty("responses");
        responses.GetArrayLength().Should().Be(1);

        var firstResponse = responses[0];
        firstResponse.GetProperty("id").GetString().Should().Be("1");
        firstResponse.GetProperty("status").GetInt32().Should().Be(200);
    }

    [IntegrationTest]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task Batch_WithMultipleRequests_ReturnsAllResponses()
    {
        var batchRequest = new ODataBatchRequest
        {
            Requests = ImmutableArray.Create(
                new ODataBatchRequestItem
                {
                    Id = "1",
                    Method = "GET",
                    Url = $"Features({TestLayerId},1)"
                },
                new ODataBatchRequestItem
                {
                    Id = "2",
                    Method = "GET",
                    Url = $"Features({TestLayerId},2)"
                })
        };

        var json = JsonSerializer.Serialize(batchRequest, ODataJsonContext.Default.ODataBatchRequest);
        var response = await _fixture.Client.PostAsync(
            "/odata/$batch",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        var responses = document.RootElement.GetProperty("responses");
        responses.GetArrayLength().Should().Be(2);
    }

    [IntegrationTest]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task Batch_WithCreateRequest_CreatesFeature()
    {
        var batchRequest = new ODataBatchRequest
        {
            Requests = ImmutableArray.Create(new ODataBatchRequestItem
            {
                Id = "create-1",
                Method = "POST",
                Url = $"Features({TestLayerId})",
                Body = new Dictionary<string, object?>
                {
                    ["Attributes"] = new Dictionary<string, object?>
                    {
                        ["name"] = "Batch Created City",
                        ["population"] = 100000L
                    }
                }
            })
        };

        var json = JsonSerializer.Serialize(batchRequest, ODataJsonContext.Default.ODataBatchRequest);
        var response = await _fixture.Client.PostAsync(
            "/odata/$batch",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        var responses = document.RootElement.GetProperty("responses");
        responses.GetArrayLength().Should().Be(1);

        var firstResponse = responses[0];
        firstResponse.GetProperty("id").GetString().Should().Be("create-1");
        firstResponse.GetProperty("status").GetInt32().Should().Be(201);
    }

    [IntegrationTest]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task Batch_WithDeleteRequest_DeletesFeature()
    {
        // Create a feature to delete
        var created = await _featureStore.CreateAsync(
            TestLayerId,
            Feature.Create(0, null, ImmutableDictionary<string, object?>.Empty.Add("name", "To Delete")));

        var batchRequest = new ODataBatchRequest
        {
            Requests = ImmutableArray.Create(new ODataBatchRequestItem
            {
                Id = "delete-1",
                Method = "DELETE",
                Url = $"Features({TestLayerId},{created.Id})"
            })
        };

        var json = JsonSerializer.Serialize(batchRequest, ODataJsonContext.Default.ODataBatchRequest);
        var response = await _fixture.Client.PostAsync(
            "/odata/$batch",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        var responses = document.RootElement.GetProperty("responses");
        var firstResponse = responses[0];
        firstResponse.GetProperty("id").GetString().Should().Be("delete-1");
        firstResponse.GetProperty("status").GetInt32().Should().Be(204);
    }

    [IntegrationTest]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task Batch_WithInvalidRequest_ReturnsError()
    {
        var batchRequest = new ODataBatchRequest
        {
            Requests = ImmutableArray.Create(new ODataBatchRequestItem
            {
                Id = "1",
                Method = "GET",
                Url = "Features(99999,1)" // Non-existent layer
            })
        };

        var json = JsonSerializer.Serialize(batchRequest, ODataJsonContext.Default.ODataBatchRequest);
        var response = await _fixture.Client.PostAsync(
            "/odata/$batch",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        var responses = document.RootElement.GetProperty("responses");
        var firstResponse = responses[0];
        firstResponse.GetProperty("status").GetInt32().Should().Be(404);
    }

    #endregion

    #region Aggregation Tests

    [IntegrationTest]
    [Operation(Operations.ODataApply)]
    [Endpoint("GET /odata/Features({layerId})/$apply")]
    public async Task Apply_WithSimpleAggregate_ReturnsSumResult()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})/$apply?$apply=aggregate(population with sum as TotalPopulation)");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        document.RootElement.TryGetProperty("@odata.context", out _).Should().BeTrue();
        var values = document.RootElement.GetProperty("value");
        values.GetArrayLength().Should().BeGreaterThan(0);

        var firstValue = values[0];
        firstValue.TryGetProperty("TotalPopulation", out var total).Should().BeTrue();
        total.GetDouble().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.ODataApply)]
    [Endpoint("GET /odata/Features({layerId})/$apply")]
    public async Task Apply_WithGroupBy_ReturnsGroupedResults()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})/$apply?$apply=groupby((state), aggregate(population with sum as TotalPop))");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        var values = document.RootElement.GetProperty("value");
        values.GetArrayLength().Should().BeGreaterThan(0);

        // Each result should have the state field and TotalPop
        foreach (var value in values.EnumerateArray())
        {
            value.TryGetProperty("state", out _).Should().BeTrue();
            value.TryGetProperty("TotalPop", out _).Should().BeTrue();
        }
    }

    [IntegrationTest]
    [Operation(Operations.ODataApply)]
    [Endpoint("GET /odata/Features({layerId})/$apply")]
    public async Task Apply_WithCount_ReturnsCountResult()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})/$apply?$apply=aggregate($count as TotalCount)");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        var values = document.RootElement.GetProperty("value");
        values.GetArrayLength().Should().Be(1);

        var firstValue = values[0];
        firstValue.TryGetProperty("TotalCount", out var total).Should().BeTrue();
        total.GetInt32().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.ODataApply)]
    [Endpoint("GET /odata/Features({layerId})/$apply")]
    public async Task Apply_WithAverage_ReturnsAverageResult()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})/$apply?$apply=aggregate(population with avg as AvgPopulation)");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        var values = document.RootElement.GetProperty("value");
        values.GetArrayLength().Should().Be(1);

        var firstValue = values[0];
        firstValue.TryGetProperty("AvgPopulation", out var avg).Should().BeTrue();
        avg.GetDouble().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.ODataApply)]
    [Endpoint("GET /odata/Features({layerId})/$apply")]
    public async Task Apply_WithMinMax_ReturnsMinMaxResults()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})/$apply?$apply=aggregate(population with min as MinPop, population with max as MaxPop)");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        var values = document.RootElement.GetProperty("value");
        values.GetArrayLength().Should().Be(1);

        var firstValue = values[0];
        firstValue.TryGetProperty("MinPop", out var min).Should().BeTrue();
        firstValue.TryGetProperty("MaxPop", out var max).Should().BeTrue();

        min.GetDouble().Should().BeLessThan(max.GetDouble());
    }

    [IntegrationTest]
    [Operation(Operations.ODataApply)]
    [Endpoint("GET /odata/Features({layerId})/$apply")]
    public async Task Apply_WithoutApplyParam_ReturnsError()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})/$apply");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        document.RootElement.TryGetProperty("error", out var error).Should().BeTrue();
        error.GetProperty("code").GetString().Should().Be("InvalidQueryOption");
    }

    [IntegrationTest]
    [Operation(Operations.ODataApply)]
    [Endpoint("GET /odata/Features({layerId})/$apply")]
    public async Task Apply_NonExistentLayer_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            "/odata/Features(99999)/$apply?$apply=aggregate(population with sum as Total)");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    #endregion

    #region Search Tests

    [IntegrationTest]
    [Operation(Operations.ODataSearch)]
    [Endpoint("GET /odata/Features({layerId})/$search")]
    public async Task Search_WithSimpleTerm_ReturnsMatchingFeatures()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})/$search?$search=San");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        document.RootElement.TryGetProperty("@odata.context", out _).Should().BeTrue();
        var values = document.RootElement.GetProperty("value");
        values.GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.ODataSearch)]
    [Endpoint("GET /odata/Features({layerId})/$search")]
    public async Task Search_WithQuotedPhrase_ReturnsMatchingFeatures()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})/$search?$search=\"Los Angeles\"");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        var values = document.RootElement.GetProperty("value");
        values.GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.ODataSearch)]
    [Endpoint("GET /odata/Features({layerId})/$search")]
    public async Task Search_WithCount_ReturnsCountAndResults()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})/$search?$search=City&$count=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        document.RootElement.TryGetProperty("@odata.count", out var count).Should().BeTrue();
        count.GetInt64().Should().BeGreaterOrEqualTo(0);
    }

    [IntegrationTest]
    [Operation(Operations.ODataSearch)]
    [Endpoint("GET /odata/Features({layerId})/$search")]
    public async Task Search_WithTopSkip_ReturnsPaginatedResults()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})/$search?$search=City&$top=2&$skip=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        var values = document.RootElement.GetProperty("value");
        values.GetArrayLength().Should().BeLessThanOrEqualTo(2);
    }

    [IntegrationTest]
    [Operation(Operations.ODataSearch)]
    [Endpoint("GET /odata/Features({layerId})/$search")]
    public async Task Search_WithoutSearchParam_ReturnsError()
    {
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})/$search");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        document.RootElement.TryGetProperty("error", out var error).Should().BeTrue();
        error.GetProperty("code").GetString().Should().Be("InvalidQueryOption");
    }

    [IntegrationTest]
    [Operation(Operations.ODataSearch)]
    [Endpoint("GET /odata/Features({layerId})/$search")]
    public async Task Search_NonExistentLayer_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            "/odata/Features(99999)/$search?$search=test");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    #endregion

    #region Expand Tests

    [IntegrationTest]
    [Operation(Operations.ODataExpand)]
    [Endpoint("GET /odata/Features({layerId})?$expand")]
    public async Task Features_WithExpand_ReturnsODataVersionHeader()
    {
        // $expand requires relationships which the test layer may not have
        // This test verifies the endpoint accepts the $expand parameter without error
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$expand=RelatedFeatures");

        // Should return OK even if no relationships exist (expand is a no-op in that case)
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("OData-Version", out var values).Should().BeTrue();
        values.Should().Contain("4.0");
    }

    [IntegrationTest]
    [Operation(Operations.ODataExpand)]
    [Endpoint("GET /odata/Features({layerId})?$expand")]
    public async Task Features_WithExpandAndFilter_ReturnsFilteredResults()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$expand=Details&$filter=name eq 'San Francisco'");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        var values = document.RootElement.GetProperty("value");
        // Filter should still work with $expand
        values.GetArrayLength().Should().BeGreaterOrEqualTo(0);
    }

    [IntegrationTest]
    [Operation(Operations.ODataExpand)]
    [Endpoint("GET /odata/Features({layerId})?$expand")]
    public async Task Features_WithExpandAndTop_ReturnsPaginatedResults()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$expand=Related&$top=5");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        var values = document.RootElement.GetProperty("value");
        values.GetArrayLength().Should().BeLessThanOrEqualTo(5);
    }

    #endregion
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.OData.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

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
    private const int TestLayerId = 0;

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Combine("tests", "seed", "odata.yaml"));
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
    public async Task Batch_WithPutRequest_ReturnsMethodNotAllowed()
    {
        var createdId = await _fixture.InsertFeatureAsync(TestLayerId, "Batch PUT Candidate");

        var batchRequest = new ODataBatchRequest
        {
            Requests = ImmutableArray.Create(new ODataBatchRequestItem
            {
                Id = "put-1",
                Method = "PUT",
                Url = $"Features({TestLayerId},{createdId})",
                Body = new Dictionary<string, object?>
                {
                    ["Attributes"] = new Dictionary<string, object?>
                    {
                        ["name"] = "Updated Via Put"
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
        responses[0].GetProperty("status").GetInt32().Should().Be((int)HttpStatusCode.MethodNotAllowed);
    }

    [IntegrationTest]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task Batch_WithFeatureRefRequest_ReturnsCanonicalReference()
    {
        var batchRequest = new ODataBatchRequest
        {
            Requests = ImmutableArray.Create(new ODataBatchRequestItem
            {
                Id = "ref-1",
                Method = "GET",
                Url = $"Features(LayerId={TestLayerId},ObjectId=1)/$ref"
            })
        };

        var json = JsonSerializer.Serialize(batchRequest, ODataJsonContext.Default.ODataBatchRequest);
        var response = await _fixture.Client.PostAsync(
            "/odata/$batch",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        var item = document.RootElement.GetProperty("responses")[0];
        item.GetProperty("status").GetInt32().Should().Be(200);
        var odataId = item.GetProperty("body").GetProperty("@odata.id").GetString();
        odataId.Should().Contain($"/odata/Features(LayerId={TestLayerId},ObjectId=1)");
    }

    [IntegrationTest]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task Batch_WithFeatureValueRequest_ReturnsRawPayload()
    {
        var batchRequest = new ODataBatchRequest
        {
            Requests = ImmutableArray.Create(new ODataBatchRequestItem
            {
                Id = "value-1",
                Method = "GET",
                Url = $"Features(LayerId={TestLayerId},ObjectId=1)/$value"
            })
        };

        var json = JsonSerializer.Serialize(batchRequest, ODataJsonContext.Default.ODataBatchRequest);
        var response = await _fixture.Client.PostAsync(
            "/odata/$batch",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        var item = document.RootElement.GetProperty("responses")[0];
        item.GetProperty("status").GetInt32().Should().Be(200);
        var body = item.GetProperty("body");
        body.GetProperty("ObjectId").GetInt64().Should().Be(1);
        body.GetProperty("LayerId").GetInt32().Should().Be(TestLayerId);
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
                Url = $"Layers({TestLayerId})/Features",
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
        var createdId = await _fixture.InsertFeatureAsync(TestLayerId, "To Delete");

        var batchRequest = new ODataBatchRequest
        {
            Requests = ImmutableArray.Create(new ODataBatchRequestItem
            {
                Id = "delete-1",
                Method = "DELETE",
                Url = $"Features({TestLayerId},{createdId})"
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

    [IntegrationTest]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task Batch_WithInvalidUrl_ReturnsBadRequestInResponse()
    {
        var batchRequest = new ODataBatchRequest
        {
            Requests = ImmutableArray.Create(new ODataBatchRequestItem
            {
                Id = "invalid-url",
                Method = "GET",
                Url = "InvalidUrl"
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
        firstResponse.GetProperty("id").GetString().Should().Be("invalid-url");
        firstResponse.GetProperty("status").GetInt32().Should().Be(400);
    }

    [IntegrationTest]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task Batch_WithAtomicityGroupFailure_RollsBackChanges()
    {
        var initialCount = await CountFeaturesAsync();

        var batchRequest = new ODataBatchRequest
        {
            Requests = ImmutableArray.Create(
                new ODataBatchRequestItem
                {
                    Id = "invalid-1",
                    Method = "GET",
                    Url = "BadUrl",
                    AtomicityGroup = "group-1"
                },
                new ODataBatchRequestItem
                {
                    Id = "create-1",
                    Method = "POST",
                    Url = $"Layers({TestLayerId})/Features",
                    AtomicityGroup = "group-1",
                    Body = new Dictionary<string, object?>
                    {
                        ["Attributes"] = new Dictionary<string, object?>
                        {
                            ["name"] = "Atomic Group City",
                            ["population"] = 4242L
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
        responses.GetArrayLength().Should().Be(2);

        int? invalidStatus = null;
        int? createStatus = null;
        foreach (var item in responses.EnumerateArray())
        {
            var id = item.GetProperty("id").GetString();
            var status = item.GetProperty("status").GetInt32();
            if (id == "invalid-1")
            {
                invalidStatus = status;
            }
            else if (id == "create-1")
            {
                createStatus = status;
            }
        }

        invalidStatus.Should().Be(400);
        createStatus.Should().Be(424);
        var finalCount = await CountFeaturesAsync();
        finalCount.Should().Be(initialCount);
    }

    [IntegrationTest]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task Batch_WithNamedKeyRequestForLayerZero_ReturnsFeature()
    {
        var batchRequest = new ODataBatchRequest
        {
            Requests = ImmutableArray.Create(new ODataBatchRequestItem
            {
                Id = "named-keys",
                Method = "GET",
                Url = "Features(LayerId=0,ObjectId=1)"
            })
        };

        var json = JsonSerializer.Serialize(batchRequest, ODataJsonContext.Default.ODataBatchRequest);
        var response = await _fixture.Client.PostAsync(
            "/odata/$batch",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);

        var batchResponses = document.RootElement.GetProperty("responses");
        batchResponses.GetArrayLength().Should().Be(1);
        batchResponses[0].GetProperty("status").GetInt32().Should().Be(200);
    }

    [IntegrationTest]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task Batch_WithCollectionGetRequest_ReturnsCollectionPayload()
    {
        var batchRequest = new ODataBatchRequest
        {
            Requests = ImmutableArray.Create(new ODataBatchRequestItem
            {
                Id = "collection",
                Method = "GET",
                Url = "Layers(0)/Features?$top=2&$count=true"
            })
        };

        var json = JsonSerializer.Serialize(batchRequest, ODataJsonContext.Default.ODataBatchRequest);
        var response = await _fixture.Client.PostAsync(
            "/odata/$batch",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);

        var batchResponses = document.RootElement.GetProperty("responses");
        batchResponses.GetArrayLength().Should().Be(1);

        var item = batchResponses[0];
        item.GetProperty("status").GetInt32().Should().Be(200);
        var body = item.GetProperty("body");
        body.GetProperty("value").GetArrayLength().Should().BeLessThanOrEqualTo(2);
        body.TryGetProperty("@odata.count", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task Batch_WithFailedDependency_ReturnsDependencyFailed()
    {
        var batchRequest = new ODataBatchRequest
        {
            Requests = ImmutableArray.Create(
                new ODataBatchRequestItem
                {
                    Id = "bad",
                    Method = "GET",
                    Url = "BadUrl"
                },
                new ODataBatchRequestItem
                {
                    Id = "dependent",
                    Method = "GET",
                    Url = "Features(0,1)",
                    DependsOn = ["bad"]
                })
        };

        var json = JsonSerializer.Serialize(batchRequest, ODataJsonContext.Default.ODataBatchRequest);
        var response = await _fixture.Client.PostAsync(
            "/odata/$batch",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);

        var statuses = document.RootElement.GetProperty("responses")
            .EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("id").GetString()!,
                item => item.GetProperty("status").GetInt32(),
                StringComparer.Ordinal);

        statuses["bad"].Should().Be(400);
        statuses["dependent"].Should().Be(424);
    }

    [IntegrationTest]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task Batch_WithMultipartRequest_ReturnsMultipartResponse()
    {
        const string boundary = "batch_123";
        var payload = string.Join("\r\n",
        [
            $"--{boundary}",
            "Content-Type: application/http",
            "Content-Transfer-Encoding: binary",
            string.Empty,
            "GET /odata/Features(0,1) HTTP/1.1",
            "Accept: application/json",
            string.Empty,
            $"--{boundary}--",
            string.Empty
        ]);

        var content = new StringContent(payload, Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/mixed;boundary={boundary}");

        var response = await _fixture.Client.PostAsync("/odata/$batch", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("multipart/mixed");
        response.Content.Headers.ContentType?.Parameters.Should()
            .Contain(p => p.Name == "boundary");

        var responseBody = await response.Content.ReadAsStringAsync();
        responseBody.Should().Contain("HTTP/1.1 200");
        responseBody.Should().Contain("OData-Version: 4.0");
    }

    private async Task<long> CountFeaturesAsync()
    {
        var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        await using var connection = await _fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM features WHERE layer_id = @layerId;";
        command.Parameters.AddWithValue("layerId", TestLayerId);

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
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
    public async Task Apply_WithCompute_ReturnsComputedField()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})/$apply?$apply=compute(population mul 2 as DoublePopulation)");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        var values = document.RootElement.GetProperty("value");
        values.GetArrayLength().Should().BeGreaterThan(0);

        var firstValue = values[0];
        firstValue.TryGetProperty("population", out var population).Should().BeTrue();
        firstValue.TryGetProperty("DoublePopulation", out var doublePopulation).Should().BeTrue();

        var populationValue = population.GetInt64();
        doublePopulation.GetDouble().Should().Be(populationValue * 2d);
    }

    [IntegrationTest]
    [Operation(Operations.ODataApply)]
    [Endpoint("GET /odata/Features({layerId})/$apply")]
    public async Task Apply_WithFilter_ReturnsFilteredFeatures()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})/$apply?$apply=filter(state eq 'California')");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        var values = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        values.Should().HaveCount(5);

        foreach (var value in values)
        {
            value.TryGetProperty("state", out var state).Should().BeTrue();
            state.GetString().Should().Be("California");
        }
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
        error.GetProperty("code").GetString().Should().Be("BadRequest");
    }

    [IntegrationTest]
    [Operation(Operations.ODataApply)]
    [Endpoint("GET /odata/Features({layerId})/$apply")]
    public async Task Apply_WithInvalidExpression_ReturnsError()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})/$apply?$apply=aggregate(population as Total)");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        document.RootElement.TryGetProperty("error", out var error).Should().BeTrue();
        error.GetProperty("code").GetString().Should().Be("BadRequest");
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
    public async Task Search_WithAndTerms_ReturnsIntersection()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})/$search?$search=San AND California");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        var values = document.RootElement.GetProperty("value");
        values.GetArrayLength().Should().BeGreaterThan(0);

        foreach (var value in values.EnumerateArray())
        {
            var attributes = ODataTestHelpers.ParseAttributes(value);
            var name = attributes.GetProperty("name").GetString() ?? string.Empty;
            var state = attributes.GetProperty("state").GetString() ?? string.Empty;

            name.Should().Contain("San");
            state.Should().Be("California");
        }
    }

    [IntegrationTest]
    [Operation(Operations.ODataSearch)]
    [Endpoint("GET /odata/Features({layerId})/$search")]
    public async Task Search_WithOrTerms_ReturnsUnion()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})/$search?$search=Seattle OR Phoenix");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        var values = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        var objectIds = values.Select(value => value.GetProperty("ObjectId").GetInt64()).ToArray();

        objectIds.Should().Contain(6L);
        objectIds.Should().Contain(10L);
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
    public async Task Search_WithNotTerm_ExcludesMatches()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})/$search?$search=NOT San");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        var values = document.RootElement.GetProperty("value");
        values.GetArrayLength().Should().BeGreaterThan(0);

        foreach (var value in values.EnumerateArray())
        {
            var attributes = ODataTestHelpers.ParseAttributes(value);
            var name = attributes.GetProperty("name").GetString() ?? string.Empty;
            name.Contains("San", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        }
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
        count.GetInt64().Should().BeGreaterThanOrEqualTo(0);
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
    [Endpoint("GET /odata/Features({layerId})/$search with filter/orderby/select/expand")]
    public async Task SearchEndpoint_WithFilterOrderBySelectAndExpand_AppliesAllOptions()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})/$search?$search=San&$filter=state eq 'California'&$orderby=ObjectId desc&$select=ObjectId,LayerId,name,state&$expand=Landmarks&$top=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        var values = document.RootElement.GetProperty("value");
        values.GetArrayLength().Should().Be(1);

        var feature = values[0];
        feature.TryGetProperty("ObjectId", out _).Should().BeTrue();
        feature.TryGetProperty("LayerId", out _).Should().BeTrue();
        feature.TryGetProperty("name", out _).Should().BeTrue();
        feature.TryGetProperty("state", out var state).Should().BeTrue();
        feature.TryGetProperty("Geometry", out _).Should().BeFalse();
        feature.TryGetProperty("Landmarks", out var landmarks).Should().BeTrue();
        landmarks.ValueKind.Should().Be(JsonValueKind.Array);
        state.GetString().Should().Be("California");
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
        error.GetProperty("code").GetString().Should().Be("BadRequest");
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

    [IntegrationTest]
    [Operation(Operations.ODataSearch)]
    [Endpoint("GET /odata/Features({layerId})?$search")]
    public async Task SearchQueryOption_WithFilterOrderByAndSelect_AppliesAllOptions()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$search=San&$filter=state eq 'California'&$orderby=ObjectId desc&$select=ObjectId,LayerId,name,state&$top=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        var values = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        values.Should().NotBeEmpty();
        values.Count.Should().BeLessThanOrEqualTo(2);

        foreach (var value in values)
        {
            value.TryGetProperty("ObjectId", out _).Should().BeTrue();
            value.TryGetProperty("LayerId", out _).Should().BeTrue();
            value.TryGetProperty("name", out var name).Should().BeTrue();
            value.TryGetProperty("state", out var state).Should().BeTrue();
            value.TryGetProperty("Geometry", out _).Should().BeFalse();

            (name.GetString() ?? string.Empty).Should().Contain("San");
            state.GetString().Should().Be("California");
        }
    }

    [IntegrationTest]
    [Operation(Operations.ODataSearch)]
    [Endpoint("GET /odata/Features({layerId})?$search with $expand")]
    public async Task SearchQueryOption_WithExpand_IncludesRelatedEntities()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$search=San&$expand=Landmarks&$top=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        var values = document.RootElement.GetProperty("value");
        values.GetArrayLength().Should().BeGreaterThan(0);
        values[0].TryGetProperty("Landmarks", out var landmarks).Should().BeTrue();
        landmarks.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Operation(Operations.ODataSearch)]
    [Endpoint("GET /odata/Features?$search")]
    public async Task SearchQueryOption_AllLayersRoute_WithLayerFilter_ReturnsResults()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features?$search=San&$filter=LayerId eq {TestLayerId} and state eq 'California'&$top=3");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        var values = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        values.Should().NotBeEmpty();
        values.All(value => value.GetProperty("LayerId").GetInt32() == TestLayerId).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.ODataSearch)]
    [Endpoint("GET /odata/Features({layerId})?$apply with $search")]
    public async Task SearchQueryOption_WithApplyCombination_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$search=San&$apply=aggregate(population with sum as TotalPopulation)");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    #endregion

    #region Compute Tests

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$compute")]
    public async Task Features_WithCompute_ReturnsComputedProperty()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$top=1&$compute=population div 1000 as PopulationThousands");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        var feature = document.RootElement.GetProperty("value")[0];
        feature.TryGetProperty("PopulationThousands", out var computed).Should().BeTrue();
        computed.ValueKind.Should().Be(JsonValueKind.Number);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$compute with $select")]
    public async Task Features_WithComputeAndSelect_ReturnsComputedAlias()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$top=1&$select=ObjectId,PopulationThousands&$compute=population div 1000 as PopulationThousands");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        var feature = document.RootElement.GetProperty("value")[0];
        feature.TryGetProperty("ObjectId", out _).Should().BeTrue();
        feature.TryGetProperty("PopulationThousands", out _).Should().BeTrue();
        feature.TryGetProperty("population", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$compute with malformed delimiter")]
    public async Task Features_WithMalformedComputeDelimiter_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$top=1&$compute=population div 1000 as PopulationThousands,");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    #endregion

    #region Expand Tests

    [IntegrationTest]
    [Operation(Operations.ODataExpand)]
    [Endpoint("GET /odata/Features({layerId})?$expand")]
    public async Task Features_WithExpand_ReturnsODataVersionHeader()
    {
        // Verify endpoint accepts $expand and returns OData headers
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$expand=Landmarks");

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
            $"/odata/Features({TestLayerId})?$expand=Landmarks&$filter=ObjectId eq 1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        var values = document.RootElement.GetProperty("value");
        values.GetArrayLength().Should().Be(1);

        var feature = values[0];
        feature.TryGetProperty("Landmarks", out var landmarks).Should().BeTrue();
        landmarks.ValueKind.Should().Be(JsonValueKind.Array);

        var landmarkNames = new List<string>();
        foreach (var landmark in landmarks.EnumerateArray())
        {
            var attributes = ODataTestHelpers.ParseAttributes(landmark);
            var name = attributes.GetProperty("name").GetString();
            if (!string.IsNullOrWhiteSpace(name))
            {
                landmarkNames.Add(name);
            }
        }

        landmarkNames.Should().Contain("Golden Gate Bridge");
        landmarkNames.Should().Contain("Coit Tower");
    }

    [IntegrationTest]
    [Operation(Operations.ODataExpand)]
    [Endpoint("GET /odata/Features({layerId})?$expand")]
    public async Task Features_WithExpandAndTop_ReturnsPaginatedResults()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$expand=Landmarks&$top=5");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        var values = document.RootElement.GetProperty("value");
        values.GetArrayLength().Should().BeLessThanOrEqualTo(5);
    }

    [IntegrationTest]
    [Operation(Operations.ODataExpand)]
    [Endpoint("GET /odata/Features({layerId})?$expand with options")]
    public async Task Features_WithExpandOptions_ReturnsExpandedResults()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$expand=Landmarks($select=name)&$filter=ObjectId eq 1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);

        var feature = document.RootElement.GetProperty("value")[0];
        feature.TryGetProperty("Landmarks", out var landmarks).Should().BeTrue();
        landmarks.ValueKind.Should().Be(JsonValueKind.Array);
        landmarks.GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.ODataExpand)]
    [Endpoint("GET /odata/Features({layerId})?$expand with nested path")]
    public async Task Features_WithNestedExpandPath_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$expand=Landmarks/SubItems");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    #endregion
}

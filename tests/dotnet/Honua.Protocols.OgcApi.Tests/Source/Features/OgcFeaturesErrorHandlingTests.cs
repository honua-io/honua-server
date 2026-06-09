// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

[Collection("Database")]
[Protocol(TestProtocols.OgcApiFeatures)]
public class OgcFeaturesErrorHandlingTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}")]
    public async Task GetCollection_NonExistentCollection_ReturnsStandardizedErrorFormat()
    {
        // Act - Request non-existent collection
        var response = await _fixture.Client.GetAsync("/ogc/features/collections/non-existent");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var content = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<JsonElement>(content);

        problem.GetProperty("type").GetString().Should().NotBeNullOrEmpty();
        problem.GetProperty("title").GetString().Should().Be("Not Found");
        problem.GetProperty("status").GetInt32().Should().Be(404);
        problem.GetProperty("detail").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/queryables")]
    public async Task GetQueryables_NonExistentCollection_ReturnsStandardizedErrorFormat()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/ogc/features/collections/non-existent/queryables");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var content = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<JsonElement>(content);

        problem.GetProperty("title").GetString().Should().Be("Not Found");
        problem.GetProperty("status").GetInt32().Should().Be(404);
        problem.GetProperty("detail").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_NonExistentCollection_ReturnsStandardizedErrorFormat()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/ogc/features/collections/non-existent/items");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var content = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<JsonElement>(content);

        problem.GetProperty("title").GetString().Should().Be("Not Found");
        problem.GetProperty("status").GetInt32().Should().Be(404);
        problem.GetProperty("detail").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    [Operation(Operations.GetById)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task GetItem_NonExistentFeature_ReturnsStandardizedErrorFormat()
    {
        // Act - Use valid collection but non-existent feature
        var response = await _fixture.Client.GetAsync("/ogc/features/collections/0/items/non-existent");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var content = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<JsonElement>(content);

        problem.GetProperty("title").GetString().Should().Be("Not Found");
        problem.GetProperty("status").GetInt32().Should().Be(404);
    }

    [Fact]
    [Operation(Operations.GetById)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task GetItem_FeatureIdWithInvalidEscapedSequence_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync("/ogc/features/collections/0/items/%25E0%25A4%25A");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var content = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<JsonElement>(content);
        problem.GetProperty("title").GetString().Should().Be("Not Found");
        problem.GetProperty("status").GetInt32().Should().Be(404);
    }

    [Fact]
    [Operation(Operations.GetById)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task GetItem_FeatureIdWithEncodedPercent_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync("/ogc/features/collections/0/items/%25");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var content = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<JsonElement>(content);
        problem.GetProperty("title").GetString().Should().Be("Not Found");
        problem.GetProperty("status").GetInt32().Should().Be(404);
    }

    [Fact]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    public async Task CreateFeature_InvalidRequest_ReturnsStandardizedErrorFormat()
    {
        // Arrange - Invalid GeoJSON payload
        var invalidPayload = "{ \"invalid\": \"json\" }";
        var content = new StringContent(invalidPayload, System.Text.Encoding.UTF8, "application/geo+json");

        // Act
        var response = await _fixture.Client.PostAsync("/ogc/features/collections/0/items", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var responseContent = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<JsonElement>(responseContent);

        problem.GetProperty("title").GetString().Should().Be("Bad Request");
        problem.GetProperty("status").GetInt32().Should().Be(400);
        problem.GetProperty("detail").GetString().Should().Be("GeoJSON payload must include a 'type' member.");
    }

    [Fact]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task UpdateFeature_InvalidRequest_ReturnsStandardizedErrorFormat()
    {
        // Arrange - Invalid GeoJSON payload
        var existingId = await _fixture.InsertFeatureAsync(0, "Invalid Update Target");
        var invalidPayload = "{ \"invalid\": \"json\" }";
        var content = new StringContent(invalidPayload, System.Text.Encoding.UTF8, "application/geo+json");

        // Act
        var response = await _fixture.Client.PutAsync($"/ogc/features/collections/0/items/{existingId}", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var responseContent = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<JsonElement>(responseContent);

        problem.GetProperty("title").GetString().Should().Be("Bad Request");
        problem.GetProperty("status").GetInt32().Should().Be(400);
        problem.GetProperty("detail").GetString().Should().Be("GeoJSON payload must include a 'type' member.");
    }

    [Fact]
    [Operation(Operations.Delete)]
    [Endpoint("DELETE /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task DeleteFeature_NonExistentFeature_ReturnsStandardizedErrorFormat()
    {
        // Act
        var response = await _fixture.Client.DeleteAsync("/ogc/features/collections/0/items/non-existent");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var content = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<JsonElement>(content);

        problem.GetProperty("title").GetString().Should().Be("Not Found");
        problem.GetProperty("status").GetInt32().Should().Be(404);
    }

    [Fact]
    public async Task OgcFeaturesErrors_UseProblemDetails_WhileFeatureServerUsesGeoServices()
    {
        // Act - Get error from OGC API Features endpoint
        var ogcResponse = await _fixture.Client.GetAsync("/ogc/features/collections/non-existent");

        // Act - Get error from FeatureServer endpoint for comparison
        var fsResponse = await _fixture.Client.GetAsync("/rest/services/non-existent/FeatureServer");

        // Assert - OGC uses RFC 7807, FeatureServer uses GeoServices
        ogcResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        fsResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var ogcContent = await ogcResponse.Content.ReadAsStringAsync();
        var fsContent = await fsResponse.Content.ReadAsStringAsync();

        var ogcProblem = JsonSerializer.Deserialize<JsonElement>(ogcContent);
        var fsError = JsonSerializer.Deserialize<ApiErrorResponse>(fsContent);

        ogcProblem.GetProperty("type").GetString().Should().NotBeNullOrEmpty();
        ogcProblem.GetProperty("status").GetInt32().Should().Be(404);

        fsError.Should().NotBeNull();

        fsError!.Error.Code.Should().Be(404);
        fsError.Error.Message.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task OgcFeaturesErrors_IncludeCorrelationId_ForRequestTracing()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/ogc/features/collections/non-existent");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Should include correlation ID header for request tracing
        response.Headers.Should().ContainKey("X-Correlation-ID");
        var correlationId = response.Headers.GetValues("X-Correlation-ID").First();
        correlationId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task OgcFeaturesErrors_NoSensitiveInformationExposed_InErrorMessages()
    {
        // Act - Trigger an internal server error scenario if possible
        var response = await _fixture.Client.GetAsync("/ogc/features/collections/0/items?limit=999999999");

        // Assert
        if (response.StatusCode == HttpStatusCode.InternalServerError)
        {
            var content = await response.Content.ReadAsStringAsync();
            var problem = JsonSerializer.Deserialize<JsonElement>(content);

            // Should not contain sensitive information like stack traces, connection strings, etc.
            content.Should().NotContain("Exception");
            content.Should().NotContain("StackTrace");
            content.Should().NotContain("ConnectionString");
            content.Should().NotContain("Database");
            content.Should().NotContain("Server=");

            // Should have generic error message
            problem.GetProperty("title").GetString().Should().NotBeNullOrEmpty();
        }
    }
}

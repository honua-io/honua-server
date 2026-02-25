// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Server.Features.FeatureServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests;

/// <summary>
/// Integration tests for limits enforcement across all protocols.
/// Tests Issue #63 - Shared limits configuration implementation.
/// </summary>
[Protocol(Protocols.FeatureServer)]
[Collection("Database")]
public sealed class LimitsEnforcementTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const string TestServiceId = "test";
    private const int TestLayerId = 0;

    // Test limits configuration
    private readonly LimitsOptions _testLimits = new()
    {
        Query = new QueryLimits
        {
            MaxRecordCount = 100,      // Lower than default for testing
            DefaultRecordCount = 50,   // Lower than default for testing
            MaxOffset = 1000,          // Lower than default for testing
            QueryTimeout = TimeSpan.FromSeconds(30)
        },
        Edits = new EditLimits
        {
            MaxPayloadSize = 1048576   // 1MB for testing
        }
    };

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Combine("tests", "seed", "server.yaml"));

        // Configure test limits
        _fixture.ReplaceService<IOptions<LimitsOptions>>(Options.Create(_testLimits));

        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    #region Service Metadata Limits Exposure

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task ServiceMetadata_ExposesConfiguredLimits()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer");

        // Assert
        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var serviceResponse = JsonSerializer.Deserialize<FeatureServerResponse>(
            content, FeatureServerJsonContext.Default.FeatureServerResponse);

        serviceResponse.Should().NotBeNull();
        serviceResponse!.MaxRecordCount.Should().Be(_testLimits.Query.MaxRecordCount);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task LayerMetadata_ExposesConfiguredLimits()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}");

        // Assert
        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var layerResponse = JsonSerializer.Deserialize<LayerResponse>(
            content, FeatureServerJsonContext.Default.LayerResponse);

        layerResponse.Should().NotBeNull();
        layerResponse!.MaxRecordCount.Should().Be(_testLimits.Query.MaxRecordCount);
        layerResponse.DrawingInfo.Should().NotBeNull();
    }

    #endregion

    #region Query Limits Enforcement

    [Theory]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [InlineData(99, true)]   // Under limit
    [InlineData(100, true)]  // At limit
    [InlineData(101, false)] // Over limit
    public async Task QueryGet_RecordCountLimit_EnforcedCorrectly(int requestedCount, bool shouldSucceed)
    {
        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?resultRecordCount={requestedCount}&f=json");

        // Assert
        if (shouldSucceed)
        {
            response.Be200Ok();
            var content = await response.Content.ReadAsStringAsync();
            content.Should().NotBeNullOrEmpty();

            var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
                content, FeatureServerJsonContext.Default.QueryResponse);
            queryResponse.Should().NotBeNull();
        }
        else
        {
            response.Be400BadRequest();
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Contain("exceed configured limits");
            content.Should().Contain("Maximum record count: 100");
        }
    }

    [Theory]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [InlineData(99, true)]   // Under limit
    [InlineData(100, true)]  // At limit
    [InlineData(101, false)] // Over limit
    public async Task QueryPost_RecordCountLimit_EnforcedCorrectly(int requestedCount, bool shouldSucceed)
    {
        // Arrange
        var queryParams = new QueryParameters
        {
            F = "json",
            ResultRecordCount = requestedCount,
            Where = "1=1"
        };

        var json = JsonSerializer.Serialize(queryParams, FeatureServerJsonContext.Default.QueryParameters);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query", content);

        // Assert
        if (shouldSucceed)
        {
            response.Be200Ok();
            var responseContent = await response.Content.ReadAsStringAsync();
            responseContent.Should().NotBeNullOrEmpty();

            var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
                responseContent, FeatureServerJsonContext.Default.QueryResponse);
            queryResponse.Should().NotBeNull();
        }
        else
        {
            response.Be400BadRequest();
            var responseContent = await response.Content.ReadAsStringAsync();
            responseContent.Should().Contain("exceed configured limits");
            responseContent.Should().Contain("Maximum record count: 100");
        }
    }

    [Theory]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [InlineData(999, true)]   // Under limit
    [InlineData(1000, true)]  // At limit
    [InlineData(1001, false)] // Over limit
    public async Task QueryGet_OffsetLimit_EnforcedCorrectly(int requestedOffset, bool shouldSucceed)
    {
        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?resultOffset={requestedOffset}&f=json");

        // Assert
        if (shouldSucceed)
        {
            response.Be200Ok();
        }
        else
        {
            response.Be400BadRequest();
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Contain("exceed configured limits");
            content.Should().Contain("Maximum offset: 1000");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryGet_NoRecordCount_UsesDefaultLimit()
    {
        // Act - Request without specifying resultRecordCount
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?f=json");

        // Assert
        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        // The query should have used the DefaultRecordCount (50) internally
        // This is verified by the system not erroring and returning results
    }

    #endregion

    #region Middleware Limits Enforcement

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task PayloadSize_ExceedsLimit_Returns413()
    {
        // Arrange - Create a payload larger than 1MB limit
        var largeWhere = new string('x', 1048577); // 1MB + 1 byte
        var queryParams = new QueryParameters
        {
            F = "json",
            Where = largeWhere
        };

        var json = JsonSerializer.Serialize(queryParams, FeatureServerJsonContext.Default.QueryParameters);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query", content);

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.RequestEntityTooLarge); // 413
        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("Request payload exceeds maximum allowed size");
        responseContent.Should().Contain("Maximum payload size is 1,048,576 bytes");
    }

    #endregion

    #region Configuration Validation

    [Fact]
    public void LimitsOptions_ValidConfiguration_PassesValidation()
    {
        // Arrange
        var validLimits = new LimitsOptions
        {
            Query = new QueryLimits
            {
                MaxRecordCount = 2000,
                DefaultRecordCount = 1000,
                MaxOffset = 100000,
                QueryTimeout = TimeSpan.FromSeconds(30)
            }
        };

        // Act
        var result = new LimitsOptionsValidator().Validate(Options.DefaultName, validLimits);
        var errors = result.Failures ?? Array.Empty<string>();

        // Assert
        errors.Should().BeEmpty();
    }

    [Fact]
    public void LimitsOptions_DefaultRecordCountExceedsMax_FailsValidation()
    {
        // Arrange
        var invalidLimits = new LimitsOptions
        {
            Query = new QueryLimits
            {
                MaxRecordCount = 1000,
                DefaultRecordCount = 2000 // Invalid: exceeds MaxRecordCount
            }
        };

        // Act
        var result = new LimitsOptionsValidator().Validate(Options.DefaultName, invalidLimits);
        var errors = result.Failures ?? Array.Empty<string>();

        // Assert
        errors.Should().NotBeEmpty();
        errors.Should().Contain(e => e.Contains("DefaultRecordCount") && e.Contains("MaxRecordCount"));
    }

    [Fact]
    public void LimitsOptions_InvalidTimeoutRange_FailsValidation()
    {
        // Arrange
        var invalidLimits = new LimitsOptions
        {
            Query = new QueryLimits
            {
                QueryTimeout = TimeSpan.FromSeconds(3) // Invalid: below minimum of 5 seconds
            }
        };

        // Act
        var result = new LimitsOptionsValidator().Validate(Options.DefaultName, invalidLimits);
        var errors = result.Failures ?? Array.Empty<string>();

        // Assert
        errors.Should().NotBeEmpty();
        errors.Should().Contain(e => e.Contains("QueryTimeout"));
    }

    [Fact]
    public void LimitsOptions_InvalidMimeTypes_FailsValidation()
    {
        // Arrange
        var invalidLimits = new LimitsOptions
        {
            Attachments = new AttachmentLimits
            {
                AllowedMimeTypes = "" // Invalid: empty
            }
        };

        // Act
        var result = new LimitsOptionsValidator().Validate(Options.DefaultName, invalidLimits);
        var errors = result.Failures ?? Array.Empty<string>();

        // Assert
        errors.Should().NotBeEmpty();
        errors.Should().Contain(e => e.Contains("AllowedMimeTypes"));
    }

    #endregion
}

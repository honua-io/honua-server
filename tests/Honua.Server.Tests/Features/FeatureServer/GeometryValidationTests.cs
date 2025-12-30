// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Server.Features.FeatureServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.FeatureServer;

/// <summary>
/// Integration tests for geometry validation and edge case handling.
/// Tests Issue #97 - Geometry validation and repair capabilities.
/// Tests Issue #45 - Edge case handling (nulls, large payloads, unicode).
/// </summary>
[Protocol(Protocols.FeatureServer)]
[Collection("Database")]
public sealed class GeometryValidationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const string TestServiceId = "test";
    private const int TestLayerId = 0;

    public async Task InitializeAsync()
    {
        _fixture.ReplaceService<ILayerCatalog>(new TestLayerCatalog());
        _fixture.ReplaceService<IFeatureStore>(new TestFeatureStore());
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    #region Null Geometry Handling (Issue #45)

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithNullGeometry_SucceedsWhenAllowed()
    {
        // Arrange - Feature with null geometry (attribute-only feature)
        var editsRequest = new ApplyEditsRequest
        {
            Adds = new[]
            {
                new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["name"] = "Attribute-Only Feature",
                        ["description"] = "Feature without geometry"
                    },
                    Geometry = null
                }
            }
        };

        var json = JsonSerializer.Serialize(editsRequest, FeatureServerJsonContext.Default.ApplyEditsRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits", content);

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var applyEditsResponse = JsonSerializer.Deserialize<ApplyEditsResponse>(
            responseContent, FeatureServerJsonContext.Default.ApplyEditsResponse);

        applyEditsResponse.Should().NotBeNull();
        applyEditsResponse!.AddResults.Should().HaveCount(1);
        applyEditsResponse.AddResults![0].Success.Should().BeTrue();
    }

    #endregion

    #region Null Attribute Handling (Issue #45)

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithNullAttributeValues_Succeeds()
    {
        // Arrange - Feature with null attribute values
        var editsRequest = new ApplyEditsRequest
        {
            Adds = new[]
            {
                new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["name"] = "Feature with Nulls",
                        ["description"] = null,
                        ["optional_field"] = null
                    },
                    Geometry = new GeoServicesGeometry
                    {
                        X = -122.4194,
                        Y = 37.7749
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(editsRequest, FeatureServerJsonContext.Default.ApplyEditsRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits", content);

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var applyEditsResponse = JsonSerializer.Deserialize<ApplyEditsResponse>(
            responseContent, FeatureServerJsonContext.Default.ApplyEditsResponse);

        applyEditsResponse.Should().NotBeNull();
        applyEditsResponse!.AddResults.Should().HaveCount(1);
        applyEditsResponse.AddResults![0].Success.Should().BeTrue();
    }

    #endregion

    #region Unicode Support (Issue #45)

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithUnicodeAttributes_PreservesCharactersRoundTrip()
    {
        // Arrange - Feature with unicode characters in various scripts
        var unicodeName = "日本語テスト 中文测试 한국어테스트 العربية Émoji: 🌍🏔️🌊";
        var unicodeDescription = "Spécial çharacters: àáâãäå ñ ü ß € £ ¥ © ® ™ ∞ ≠ ≤ ≥";

        var editsRequest = new ApplyEditsRequest
        {
            Adds = new[]
            {
                new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["name"] = unicodeName,
                        ["description"] = unicodeDescription
                    },
                    Geometry = new GeoServicesGeometry
                    {
                        X = 139.6917,
                        Y = 35.6895 // Tokyo
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(editsRequest, FeatureServerJsonContext.Default.ApplyEditsRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act - Add the feature
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits", content);

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var applyEditsResponse = JsonSerializer.Deserialize<ApplyEditsResponse>(
            responseContent, FeatureServerJsonContext.Default.ApplyEditsResponse);

        applyEditsResponse.Should().NotBeNull();
        applyEditsResponse!.AddResults.Should().HaveCount(1);
        applyEditsResponse.AddResults![0].Success.Should().BeTrue();
        var objectId = applyEditsResponse.AddResults[0].ObjectId;

        // Query the feature back to verify unicode is preserved
        var queryResponse = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?where=objectid={objectId}&f=json");

        queryResponse.Be200Ok();
        var queryContent = await queryResponse.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(queryContent);
        var features = document.RootElement.GetProperty("features");
        features.GetArrayLength().Should().BeGreaterThan(0);
        var attributes = features[0].GetProperty("attributes");
        attributes.GetProperty("name").GetString().Should().Be(unicodeName);
        attributes.GetProperty("description").GetString().Should().Be(unicodeDescription);
    }

    #endregion

    #region Geometry Validation (Issue #97)

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithValidPointGeometry_Succeeds()
    {
        // Arrange
        var editsRequest = new ApplyEditsRequest
        {
            Adds = new[]
            {
                new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["name"] = "Valid Point Feature"
                    },
                    Geometry = new GeoServicesGeometry
                    {
                        X = -122.4194,
                        Y = 37.7749
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(editsRequest, FeatureServerJsonContext.Default.ApplyEditsRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits", content);

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var applyEditsResponse = JsonSerializer.Deserialize<ApplyEditsResponse>(
            responseContent, FeatureServerJsonContext.Default.ApplyEditsResponse);

        applyEditsResponse.Should().NotBeNull();
        applyEditsResponse!.AddResults.Should().HaveCount(1);
        applyEditsResponse.AddResults![0].Success.Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithValidPolygonGeometry_Succeeds()
    {
        // Arrange - Valid polygon with exterior ring
        var editsRequest = new ApplyEditsRequest
        {
            Adds = new[]
            {
                new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["name"] = "Valid Polygon Feature"
                    },
                    Geometry = new GeoServicesGeometry
                    {
                        Rings = new[]
                        {
                            new[]
                            {
                                new[] { -122.5, 37.5 },
                                new[] { -122.0, 37.5 },
                                new[] { -122.0, 38.0 },
                                new[] { -122.5, 38.0 },
                                new[] { -122.5, 37.5 } // Closed ring
                            }
                        }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(editsRequest, FeatureServerJsonContext.Default.ApplyEditsRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits", content);

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var applyEditsResponse = JsonSerializer.Deserialize<ApplyEditsResponse>(
            responseContent, FeatureServerJsonContext.Default.ApplyEditsResponse);

        applyEditsResponse.Should().NotBeNull();
        applyEditsResponse!.AddResults.Should().HaveCount(1);
        applyEditsResponse.AddResults![0].Success.Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithValidLineStringGeometry_Succeeds()
    {
        // Arrange - Valid linestring
        var editsRequest = new ApplyEditsRequest
        {
            Adds = new[]
            {
                new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["name"] = "Valid LineString Feature"
                    },
                    Geometry = new GeoServicesGeometry
                    {
                        Paths = new[]
                        {
                            new[]
                            {
                                new[] { -122.5, 37.5 },
                                new[] { -122.4, 37.6 },
                                new[] { -122.3, 37.7 }
                            }
                        }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(editsRequest, FeatureServerJsonContext.Default.ApplyEditsRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits", content);

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var applyEditsResponse = JsonSerializer.Deserialize<ApplyEditsResponse>(
            responseContent, FeatureServerJsonContext.Default.ApplyEditsResponse);

        applyEditsResponse.Should().NotBeNull();
        applyEditsResponse!.AddResults.Should().HaveCount(1);
        applyEditsResponse.AddResults![0].Success.Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithZCoordinates_Succeeds()
    {
        // Arrange - Point with Z coordinate
        var editsRequest = new ApplyEditsRequest
        {
            Adds = new[]
            {
                new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["name"] = "3D Point Feature"
                    },
                    Geometry = new GeoServicesGeometry
                    {
                        X = -122.4194,
                        Y = 37.7749,
                        Z = 100.5,
                        HasZ = true
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(editsRequest, FeatureServerJsonContext.Default.ApplyEditsRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits", content);

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var applyEditsResponse = JsonSerializer.Deserialize<ApplyEditsResponse>(
            responseContent, FeatureServerJsonContext.Default.ApplyEditsResponse);

        applyEditsResponse.Should().NotBeNull();
        applyEditsResponse!.AddResults.Should().HaveCount(1);
        applyEditsResponse.AddResults![0].Success.Should().BeTrue();
    }

    #endregion

    #region Empty Result Set Handling (Issue #45)

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_WithNoMatchingFeatures_ReturnsEmptyResultSet()
    {
        // Act - Query with a WHERE clause that matches nothing
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?where=name='NonExistentFeatureName12345'&f=json");

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            responseContent, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.Features.Should().BeEmpty();
        queryResponse.ExceededTransferLimit.Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query?returnCountOnly=true")]
    public async Task Query_WithNoMatchingFeatures_ReturnsZeroCount()
    {
        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?where=name='NonExistentFeatureName12345'&returnCountOnly=true&f=json");

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            responseContent, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Count.Should().Be(0);
    }

    #endregion

    #region Spatial Query Input Validation

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_WithValidEnvelopeGeometry_Succeeds()
    {
        // Arrange - Valid envelope
        var envelopeGeometry = """{"xmin":-123,"ymin":37,"xmax":-122,"ymax":38}""";

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?geometry={Uri.EscapeDataString(envelopeGeometry)}&f=json");

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            responseContent, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_WithMalformedGeometryJson_Returns400()
    {
        // Arrange - Malformed JSON
        var malformedGeometry = """{invalid json""";

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?geometry={Uri.EscapeDataString(malformedGeometry)}&f=json");

        // Assert
        response.Be400BadRequest();
    }

    #endregion
}

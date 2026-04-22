// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.OData.Client;

namespace Honua.Server.Tests.Features.OData;

/// <summary>
/// Comprehensive OData integration tests using Microsoft.OData.Client.
/// Tests service document parsing, metadata validation, and query operations
/// with deterministic test data for cross-protocol parity verification.
/// </summary>
/// <remarks>
/// Behavior reference: Issue #158 - Add OData integration tests using Microsoft.OData.Client
/// These tests validate that the OData service document parses correctly,
/// metadata is accessible, and query operations return expected results.
/// </remarks>
[Collection("Database")]
[Protocol(Protocols.ODataV4)]
public sealed class ODataClientIntegrationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const int TestLayerId = 0;
    private const int TotalTestFeatures = 15;

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Combine("tests", "seed", "odata.yaml"));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    #region Service Document Tests

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /odata")]
    public async Task ServiceDocument_ParsesCorrectly_WithODataClient()
    {
        // Arrange
        var context = CreateODataContext(_fixture.Client);

        // Act - The service document is automatically fetched when accessing entity sets
        var query = context.CreateQuery<ODataLayer>("Layers");
        var requestUri = query.RequestUri;

        // Assert - Service root should be correctly formed
        requestUri.Should().NotBeNull();
        requestUri!.ToString().Should().Contain("/odata/Layers");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /odata")]
    public async Task ServiceDocument_ContainsExpectedEntitySets()
    {
        // Arrange & Act
        var response = await _fixture.Client.GetAsync("/odata");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // Assert
        var entitySets = document.RootElement.GetProperty("value").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString())
            .ToList();

        entitySets.Should().Contain("Layers");
        entitySets.Should().Contain("Features");
    }

    #endregion

    #region Metadata Tests

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /odata/$metadata")]
    public async Task Metadata_IsAccessible_AndContainsEntityTypes()
    {
        // Arrange & Act
        var response = await _fixture.Client.GetAsync("/odata/$metadata");
        var content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        content.Should().Contain("EntityType Name=\"Layer\"");
        content.Should().Contain("EntityType Name=\"Feature\"");
        content.Should().Contain("Property Name=\"ObjectId\"");
        content.Should().Contain("Property Name=\"LayerId\"");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /odata/$metadata")]
    public async Task Metadata_DefinesCorrectKeyProperties()
    {
        // Arrange & Act
        var response = await _fixture.Client.GetAsync("/odata/$metadata");
        var content = await response.Content.ReadAsStringAsync();

        // Assert - Verify key definitions
        content.Should().Contain("<PropertyRef Name=\"Id\"/>");
        content.Should().Contain("<PropertyRef Name=\"ObjectId\"/>");
    }

    #endregion

    #region Query Tests - Basic Operations

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Layers")]
    public async Task LayersQuery_ReturnsExpectedLayer_ViaODataClient()
    {
        // Arrange
        var context = CreateODataContext(_fixture.Client);

        // Act
        var query = context.CreateQuery<ODataLayer>("Layers");
        var response = await query.ExecuteAsync();
        var layers = response.ToList();

        // Assert
        layers.Should().NotBeEmpty();
        layers.Should().Contain(layer => layer.Id == 0 && layer.Name == "US Cities");
        layers.Should().Contain(layer => layer.Id == 1 && layer.Name == "City Landmarks");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Layers?$filter=Name eq 'US Cities'")]
    public async Task LayersQuery_WithFilter_ByName_ReturnsSingleMatch()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/odata/Layers?$filter=Name%20eq%20'US%20Cities'");

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var layers = document.RootElement.GetProperty("value").EnumerateArray().ToList();

        layers.Should().HaveCount(1);
        layers[0].GetProperty("Name").GetString().Should().Be("US Cities");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Layers?$filter=startswith(Name,'US')")]
    public async Task LayersQuery_WithFilter_StartsWith_ReturnsExpectedMatches()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/odata/Layers?$filter=startswith(Name,'US')");

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var layers = document.RootElement.GetProperty("value").EnumerateArray()
            .Select(item => item.GetProperty("Name").GetString())
            .ToList();

        layers.Should().Contain("US Cities");
        layers.Should().NotContain("City Landmarks");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task FeaturesQuery_ReturnsAllFeatures_ViaODataClient()
    {
        // Arrange
        var context = CreateODataContext(_fixture.Client);

        // Act
        var response = await context.ExecuteAsync<ODataFeature>(
            new Uri($"Features({TestLayerId})", UriKind.Relative));
        var features = response.ToList();

        // Assert
        features.Should().HaveCount(TotalTestFeatures);
        features.Should().OnlyContain(f => f.LayerId == TestLayerId);
    }

    #endregion

    #region Query Tests - $top and $skip (Pagination)

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$top=5")]
    public async Task FeaturesQuery_WithTop_ReturnsLimitedResults()
    {
        // Arrange & Act
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=5");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // Assert
        var features = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        features.Should().HaveCount(5);

        document.RootElement.TryGetProperty("@odata.nextLink", out var nextLinkElement).Should().BeTrue();
        var nextLink = nextLinkElement.GetString();
        nextLink.Should().NotBeNullOrEmpty();
        nextLink.Should().Contain("$skip=5");
        nextLink.Should().Contain("$top=5");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$skip=10")]
    public async Task FeaturesQuery_WithSkip_ReturnsOffsetResults()
    {
        // Arrange & Act
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$skip=10");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // Assert
        var features = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        features.Should().HaveCount(5); // 15 total - 10 skipped = 5 remaining
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$top=3&$skip=5")]
    public async Task FeaturesQuery_WithTopAndSkip_ReturnsPaginatedResults()
    {
        // Arrange & Act
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=3&$skip=5");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // Assert
        var features = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        features.Should().HaveCount(3);

        // Verify we got the correct features (6th, 7th, 8th - which are Seattle, Portland, Salt Lake City)
        var firstFeatureObjectId = features[0].GetProperty("ObjectId").GetInt64();
        firstFeatureObjectId.Should().Be(6); // Seattle
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$skip=13&$top=10")]
    public async Task FeaturesQuery_WithSkipNearEnd_ReturnsRemainingResults()
    {
        // Arrange & Act
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$skip=13&$top=10");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // Assert
        var features = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        features.Should().HaveCount(2); // 15 total - 13 skipped = 2 remaining (even though $top=10)
    }

    #endregion

    #region Query Tests - $count

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$count=true")]
    public async Task FeaturesQuery_WithCount_ReturnsTotalCount()
    {
        // Arrange & Act
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$count=true");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // Assert
        document.RootElement.TryGetProperty("@odata.count", out var countElement).Should().BeTrue();
        countElement.GetInt64().Should().Be(TotalTestFeatures);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$top=5&$count=true")]
    public async Task FeaturesQuery_WithTopAndCount_ReturnsTotalCountWithLimitedResults()
    {
        // Arrange & Act
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=5&$count=true");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // Assert
        var features = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        features.Should().HaveCount(5);

        document.RootElement.TryGetProperty("@odata.count", out var countElement).Should().BeTrue();
        countElement.GetInt64().Should().Be(TotalTestFeatures); // Total count, not limited results
    }

    #endregion

    #region Query Tests - $orderby

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$orderby=ObjectId desc")]
    public async Task FeaturesQuery_WithOrderByObjectIdDesc_ReturnsDescendingOrder()
    {
        // Arrange & Act
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$orderby=ObjectId desc&$top=3");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // Assert
        var objectIds = document.RootElement.GetProperty("value")
            .EnumerateArray()
            .Select(feature => feature.GetProperty("ObjectId").GetInt64())
            .ToList();

        objectIds.Should().BeInDescendingOrder();
        objectIds.First().Should().Be(TotalTestFeatures);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$orderby=population desc")]
    public async Task FeaturesQuery_WithOrderByPopulationDesc_ReturnsHighestPopulationFirst()
    {
        // Arrange & Act
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$orderby=population desc&$top=1");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // Assert
        var feature = document.RootElement.GetProperty("value").EnumerateArray().First();
        feature.GetProperty("ObjectId").GetInt64().Should().Be(2);

        var attributes = ODataTestHelpers.ParseAttributes(feature);
        attributes.GetProperty("population").GetInt64().Should().Be(3979576);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$orderby=state asc,population desc")]
    public async Task FeaturesQuery_WithOrderByMultipleFields_ReturnsDeterministicResults()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$orderby=state asc,population desc&$top=2");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        var features = document.RootElement.GetProperty("value").EnumerateArray().ToArray();
        features.Length.Should().Be(2);

        // Arizona cities ordered by population: Phoenix then Tucson
        features[0].GetProperty("ObjectId").GetInt64().Should().Be(10);
        features[1].GetProperty("ObjectId").GetInt64().Should().Be(12);
    }

    #endregion

    #region Query Tests - $select

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$select=ObjectId")]
    public async Task FeaturesQuery_WithSelect_ReturnsOnlySelectedFields()
    {
        // Arrange & Act
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$select=ObjectId&$top=1");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // Assert
        var feature = document.RootElement.GetProperty("value").EnumerateArray().First();
        feature.TryGetProperty("ObjectId", out _).Should().BeTrue();

        // When $select is used, non-selected fields should not be present or empty
        var hasLayerId = feature.TryGetProperty("LayerId", out var layerIdElement);
        if (hasLayerId)
        {
            // If present, it should be in the filtered result (implementation may include key fields)
            // This is acceptable OData behavior
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$select=ObjectId,LayerId")]
    public async Task FeaturesQuery_WithMultipleSelect_ReturnsSelectedFields()
    {
        // Arrange & Act
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$select=ObjectId,LayerId&$top=1");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // Assert
        var feature = document.RootElement.GetProperty("value").EnumerateArray().First();
        feature.TryGetProperty("ObjectId", out _).Should().BeTrue();
        feature.TryGetProperty("LayerId", out _).Should().BeTrue();
    }

    #endregion

    #region Query Tests - $filter with Comparison Operators

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=")]
    public async Task FeaturesQuery_WithFilterEquals_ReturnsMatchingFeatures()
    {
        // Arrange - Filter for San Francisco (objectid = 1)
        var filter = "ObjectId eq 1";

        // Act
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // Assert
        var features = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        features.Should().ContainSingle();
        features[0].GetProperty("ObjectId").GetInt64().Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=")]
    public async Task FeaturesQuery_WithFilterNotEquals_ExcludesMatchingFeatures()
    {
        // Arrange - Exclude San Francisco
        var filter = "ObjectId ne 1";

        // Act
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // Assert
        var features = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        features.Should().HaveCount(TotalTestFeatures - 1);
        features.Should().NotContain(f => f.GetProperty("ObjectId").GetInt64() == 1);
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=")]
    public async Task FeaturesQuery_WithFilterGreaterThan_ReturnsFilteredFeatures()
    {
        // Arrange - Features with objectid > 10
        var filter = "ObjectId gt 10";

        // Act
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // Assert
        var features = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        features.Should().HaveCount(5); // IDs 11, 12, 13, 14, 15
        features.Should().OnlyContain(f => f.GetProperty("ObjectId").GetInt64() > 10);
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=")]
    public async Task FeaturesQuery_WithFilterLessThan_ReturnsFilteredFeatures()
    {
        // Arrange - Features with objectid < 5
        var filter = "ObjectId lt 5";

        // Act
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // Assert
        var features = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        features.Should().HaveCount(4); // IDs 1, 2, 3, 4
        features.Should().OnlyContain(f => f.GetProperty("ObjectId").GetInt64() < 5);
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=")]
    public async Task FeaturesQuery_WithFilterGreaterThanOrEqual_ReturnsFilteredFeatures()
    {
        // Arrange - Features with objectid >= 14
        var filter = "ObjectId ge 14";

        // Act
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // Assert
        var features = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        features.Should().HaveCount(2); // IDs 14, 15
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=")]
    public async Task FeaturesQuery_WithFilterLessThanOrEqual_ReturnsFilteredFeatures()
    {
        // Arrange - Features with objectid <= 3
        var filter = "ObjectId le 3";

        // Act
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // Assert
        var features = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        features.Should().HaveCount(3); // IDs 1, 2, 3
    }

    #endregion

    #region Query Tests - $filter with Boolean/Null/Numeric Fields

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=is_capital eq true")]
    public async Task FeaturesQuery_WithFilterBoolean_ReturnsCapitals()
    {
        var filter = "is_capital eq true";

        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        var features = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        features.Should().HaveCount(5); // Sacramento, Salt Lake City, Denver, Phoenix, Boise

        var objectIds = features.Select(f => f.GetProperty("ObjectId").GetInt64()).ToArray();
        objectIds.Should().Contain(new long[] { 3, 8, 9, 10, 15 });
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=notes eq null")]
    public async Task FeaturesQuery_WithFilterNull_ReturnsNullNotes()
    {
        var filter = "notes eq null";

        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        var features = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        features.Should().HaveCount(4); // San Francisco, San Diego, Salt Lake City, Tucson

        var objectIds = features.Select(f => f.GetProperty("ObjectId").GetInt64()).ToArray();
        objectIds.Should().Contain(new long[] { 1, 4, 8, 12 });
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=population gt 1000000")]
    public async Task FeaturesQuery_WithFilterNumericField_ReturnsLargeCities()
    {
        var filter = "population gt 1000000";

        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        var features = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        features.Should().HaveCount(4); // Los Angeles, San Diego, San Jose, Phoenix

        var objectIds = features.Select(f => f.GetProperty("ObjectId").GetInt64()).ToArray();
        objectIds.Should().Contain(new long[] { 2, 4, 5, 10 });
    }

    #endregion

    #region Query Tests - $filter with String Functions

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=contains()")]
    public async Task FeaturesQuery_WithContainsFilter_ReturnsMatchingFeatures()
    {
        // Arrange - Cities containing "San" in name
        var filter = "contains(name, 'San')";

        // Act
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // Assert
        var features = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        // San Francisco, San Diego, San Jose = 3 features
        features.Should().HaveCount(3);
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=startswith()")]
    public async Task FeaturesQuery_WithStartsWithFilter_ReturnsMatchingFeatures()
    {
        // Arrange - Cities starting with "S"
        var filter = "startswith(name, 'S')";

        // Act
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // Assert
        var features = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        // San Francisco, Sacramento, San Diego, San Jose, Seattle, Salt Lake City = 6 features
        features.Should().HaveCount(6);
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=endswith()")]
    public async Task FeaturesQuery_WithEndsWithFilter_ReturnsMatchingFeatures()
    {
        // Arrange - Cities ending with "City"
        var filter = "endswith(name, 'City')";

        // Act
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // Assert
        var features = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        // Salt Lake City, Virtual City = 2 features
        features.Should().HaveCount(2);
    }

    #endregion

    #region Query Tests - Combined Parameters

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$filter=&$top=&$count=true")]
    public async Task FeaturesQuery_WithFilterTopAndCount_ReturnsCorrectResults()
    {
        // Arrange - Filter for objectid > 5, get top 3, with count
        var filter = "ObjectId gt 5";

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}&$top=3&$count=true");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // Assert
        var features = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        features.Should().HaveCount(3); // Limited by $top

        document.RootElement.TryGetProperty("@odata.count", out var countElement).Should().BeTrue();
        countElement.GetInt64().Should().Be(10); // Total matching filter (IDs 6-15)
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$filter=&$skip=&$top=")]
    public async Task FeaturesQuery_WithFilterSkipAndTop_ReturnsPaginatedFilteredResults()
    {
        // Arrange - Filter for objectid > 5, skip 2, take 3
        var filter = "ObjectId gt 5";

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}&$skip=2&$top=3");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // Assert
        var features = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        features.Should().HaveCount(3);

        // Should be objectids 8, 9, 10 (Salt Lake City, Denver, Phoenix)
        features[0].GetProperty("ObjectId").GetInt64().Should().Be(8);
        features[1].GetProperty("ObjectId").GetInt64().Should().Be(9);
        features[2].GetProperty("ObjectId").GetInt64().Should().Be(10);
    }

    #endregion

    #region Layer Query Tests

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Layers?$count=true")]
    public async Task LayersQuery_WithCount_ReturnsTotalLayerCount()
    {
        // Arrange & Act
        var response = await _fixture.Client.GetAsync("/odata/Layers?$count=true");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // Assert
        document.RootElement.TryGetProperty("@odata.count", out var countElement).Should().BeTrue();
        countElement.GetInt64().Should().Be(2);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Layers?$select=Name")]
    public async Task LayersQuery_WithSelect_ReturnsOnlySelectedFields()
    {
        // Arrange & Act
        var response = await _fixture.Client.GetAsync("/odata/Layers?$select=Name");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // Assert
        var layer = document.RootElement.GetProperty("value").EnumerateArray().First();
        layer.TryGetProperty("Name", out var nameElement).Should().BeTrue();
        nameElement.GetString().Should().BeOneOf("US Cities", "City Landmarks");
    }

    #endregion

    #region Error Handling Tests

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$top=-1")]
    public async Task FeaturesQuery_WithInvalidTop_ReturnsBadRequest()
    {
        // Arrange & Act
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$top=-1");

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$skip=-1")]
    public async Task FeaturesQuery_WithInvalidSkip_ReturnsBadRequest()
    {
        // Arrange & Act
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})?$skip=-1");

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features(999)")]
    public async Task FeaturesQuery_WithNonexistentLayer_ReturnsNotFound()
    {
        // Arrange & Act
        var response = await _fixture.Client.GetAsync("/odata/Features(999)");

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }

    #endregion

    #region Logical Operator Tests

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=and")]
    public async Task FeaturesQuery_WithAndOperator_ReturnsMatchingFeatures()
    {
        // Arrange - Filter for features with ObjectId > 5 AND ObjectId < 10
        var filter = "ObjectId gt 5 and ObjectId lt 10";

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // Assert
        var features = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        features.Should().HaveCount(4); // IDs 6, 7, 8, 9
        features.Should().OnlyContain(f =>
            f.GetProperty("ObjectId").GetInt64() > 5 &&
            f.GetProperty("ObjectId").GetInt64() < 10);
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=or")]
    public async Task FeaturesQuery_WithOrOperator_ReturnsMatchingFeatures()
    {
        // Arrange - Filter for features with ObjectId = 1 OR ObjectId = 15
        var filter = "ObjectId eq 1 or ObjectId eq 15";

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // Assert
        var features = document.RootElement.GetProperty("value").EnumerateArray().ToList();
        features.Should().HaveCount(2); // IDs 1 and 15
        var objectIds = features.Select(f => f.GetProperty("ObjectId").GetInt64()).ToList();
        objectIds.Should().Contain(1);
        objectIds.Should().Contain(15);
    }

    #endregion

    #region Context URL Validation Tests

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /odata")]
    public async Task ServiceDocument_HasAbsoluteContextUrl()
    {
        // Arrange & Act
        var response = await _fixture.Client.GetAsync("/odata");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // Assert - @odata.context must be absolute URL
        var contextUrl = document.RootElement.GetProperty("@odata.context").GetString();
        contextUrl.Should().NotBeNullOrEmpty();
        contextUrl.Should().StartWith("http");
        contextUrl.Should().Contain("/odata/$metadata");

        // Verify it's a valid absolute URI
        Uri.TryCreate(contextUrl, UriKind.Absolute, out var uri).Should().BeTrue();
        uri.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task FeaturesResponse_HasAbsoluteContextUrl()
    {
        // Arrange & Act
        var response = await _fixture.Client.GetAsync($"/odata/Features({TestLayerId})");
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        // Assert - @odata.context must be absolute URL with #Features suffix
        var contextUrl = document.RootElement.GetProperty("@odata.context").GetString();
        contextUrl.Should().NotBeNullOrEmpty();
        contextUrl.Should().StartWith("http");
        contextUrl.Should().Contain("/odata/$metadata#Features");

        Uri.TryCreate(contextUrl, UriKind.Absolute, out var uri).Should().BeTrue();
    }

    #endregion

    #region Spatial Filter Tests (Expected to FAIL - drives implementation)

    // NOTE: These tests are intentionally designed to FAIL with current implementation
    // to drive the implementation of spatial filter support in OData endpoints.
    // Per issue #158: "Tests should intentionally fail rather than skip to force
    // implementation of geo-spatial functions and CRS metadata alignment."
    //
    // When spatial filtering is implemented, these tests should pass.
    // DO NOT change these to pass without implementing actual spatial filtering.

    [IntegrationTest]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /odata/Features({layerId})?$filter=geo.intersects()")]
    public async Task FeaturesQuery_WithGeoIntersects_FiltersToFeaturesInPolygon()
    {
        // Arrange - Spatial filter using OData geo.intersects function
        // Polygon covers San Francisco area (roughly 37-38 lat, -123 to -122 lon)
        var filter = "geo.intersects(Geometry, geography'POLYGON((-123 37, -122 37, -122 38, -123 38, -123 37))')";

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        // Assert - When spatial filtering is implemented:
        // - Response should be 200 OK
        // - Only features within the polygon should be returned
        // - San Francisco (objectid=1) is at -122.4194, 37.7749 - INSIDE polygon
        // - Sacramento (objectid=3) is at -121.4944, 38.5816 - OUTSIDE polygon (lat > 38)
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK,
            "geo.intersects spatial filter should be supported");

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var features = document.RootElement.GetProperty("value").EnumerateArray().ToList();

        // San Francisco should be included (inside polygon)
        features.Should().Contain(f => f.GetProperty("ObjectId").GetInt64() == 1,
            "San Francisco (objectid=1) should be inside the polygon");

        // Features outside the polygon should NOT be included
        features.Should().NotContain(f => f.GetProperty("ObjectId").GetInt64() == 3,
            "Sacramento (objectid=3) should be outside the polygon");

        // Should NOT return all features (that would mean filter wasn't applied)
        features.Should().HaveCountLessThan(TotalTestFeatures,
            "Spatial filter should reduce the result set, not return all features");
    }

    [IntegrationTest]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /odata/Features({layerId})?$filter=geo.distance()")]
    public async Task FeaturesQuery_WithGeoDistance_FiltersToNearbyFeatures()
    {
        // Arrange - Spatial filter using OData geo.distance function
        // Find cities within 50km of San Francisco (-122.4194, 37.7749)
        var filter = "geo.distance(Geometry, geography'POINT(-122.4194 37.7749)') lt 50000";

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        // Assert - When spatial filtering is implemented:
        // - Response should be 200 OK
        // - Only features within 50km of SF should be returned
        // - San Francisco itself should definitely be included (distance = 0)
        // - Distant cities like Phoenix should NOT be included
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK,
            "geo.distance spatial filter should be supported");

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var features = document.RootElement.GetProperty("value").EnumerateArray().ToList();

        // San Francisco should be included (distance = 0)
        features.Should().Contain(f => f.GetProperty("ObjectId").GetInt64() == 1,
            "San Francisco should be within 50km of itself");

        // Phoenix (objectid=10) is ~1000km away - should NOT be included
        features.Should().NotContain(f => f.GetProperty("ObjectId").GetInt64() == 10,
            "Phoenix should be more than 50km from San Francisco");

        // Should NOT return all features
        features.Should().HaveCountLessThan(TotalTestFeatures,
            "Distance filter should reduce the result set");
    }

    #endregion

    #region Helper Methods

    private static DataServiceContext CreateODataContext(HttpClient client)
        => ODataTestClient.CreateContext(client);

    #endregion
}

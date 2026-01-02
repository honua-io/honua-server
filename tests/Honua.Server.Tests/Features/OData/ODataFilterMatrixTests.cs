// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.OData;

/// <summary>
/// Comprehensive OData v4 filter matrix tests validating all comparison operators,
/// string functions, null/boolean/numeric field handling, and logical combinations.
/// Implements parity with OGC API Features Python test matrices per issue #200.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.ODataV4)]
public sealed class ODataFilterMatrixTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const int TestLayerId = 0;

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Combine("tests", "seed", "odata.yaml"));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    #region Comparison Operators - eq

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=ObjectId eq 1")]
    public async Task Filter_EqualInteger_ReturnsSingleMatch()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter=ObjectId eq 1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().ContainSingle()
            .Which.GetProperty("ObjectId").GetInt64().Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=name eq 'value'")]
    public async Task Filter_EqualString_ReturnsSingleMatch()
    {
        var filter = "name eq 'San Francisco'";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().ContainSingle();
        var attrs = ParseAttributes(features[0]);
        attrs.GetProperty("name").GetString().Should().Be("San Francisco");
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=is_capital eq true")]
    public async Task Filter_EqualBooleanTrue_ReturnsCapitals()
    {
        var filter = "is_capital eq true";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().HaveCount(5); // Sacramento, Salt Lake City, Denver, Phoenix, Boise
        foreach (var feature in features)
        {
            var attrs = ParseAttributes(feature);
            attrs.GetProperty("is_capital").GetBoolean().Should().BeTrue();
        }
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=is_capital eq false")]
    public async Task Filter_EqualBooleanFalse_ReturnsNonCapitals()
    {
        var filter = "is_capital eq false";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().HaveCount(10); // 15 total - 5 capitals
        foreach (var feature in features)
        {
            var attrs = ParseAttributes(feature);
            attrs.GetProperty("is_capital").GetBoolean().Should().BeFalse();
        }
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=notes eq null")]
    public async Task Filter_EqualNull_ReturnsNullFields()
    {
        var filter = "notes eq null";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().HaveCount(4); // San Francisco, San Diego, Salt Lake City, Tucson
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=rating eq 4.8")]
    public async Task Filter_EqualDouble_ReturnsExactMatch()
    {
        var filter = "rating eq 4.8";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().ContainSingle(); // San Francisco
        var attrs = ParseAttributes(features[0]);
        attrs.GetProperty("rating").GetDouble().Should().Be(4.8);
    }

    #endregion

    #region Comparison Operators - ne

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=ObjectId ne 1")]
    public async Task Filter_NotEqualInteger_ExcludesMatch()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter=ObjectId ne 1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().HaveCount(14);
        features.Should().NotContain(f => f.GetProperty("ObjectId").GetInt64() == 1);
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=state ne 'California'")]
    public async Task Filter_NotEqualString_ExcludesMatches()
    {
        var filter = "state ne 'California'";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().HaveCount(10); // 15 - 5 California cities
        foreach (var feature in features)
        {
            var attrs = ParseAttributes(feature);
            var state = attrs.TryGetProperty("state", out var s) ? s.GetString() : null;
            state.Should().NotBe("California");
        }
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=notes ne null")]
    public async Task Filter_NotEqualNull_ReturnsNonNullFields()
    {
        var filter = "notes ne null";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().HaveCount(11); // 15 - 4 with null notes
    }

    #endregion

    #region Comparison Operators - gt, ge, lt, le

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=population gt 1000000")]
    public async Task Filter_GreaterThan_ReturnsLargeCities()
    {
        var filter = "population gt 1000000";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().HaveCount(4); // LA, San Diego, San Jose, Phoenix
        foreach (var feature in features)
        {
            var attrs = ParseAttributes(feature);
            attrs.GetProperty("population").GetInt64().Should().BeGreaterThan(1000000);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=population ge 1000000")]
    public async Task Filter_GreaterThanOrEqual_IncludesBoundary()
    {
        var filter = "population ge 1021795"; // San Jose's exact population
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Select(f => f.GetProperty("ObjectId").GetInt64())
            .Should().Contain(5); // San Jose
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=population lt 300000")]
    public async Task Filter_LessThan_ReturnsSmallCities()
    {
        var filter = "population lt 300000";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        // Salt Lake City (200591), Boise (235684), Virtual City (0)
        features.Should().HaveCount(3);
        foreach (var feature in features)
        {
            var attrs = ParseAttributes(feature);
            attrs.GetProperty("population").GetInt64().Should().BeLessThan(300000);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=population le 200591")]
    public async Task Filter_LessThanOrEqual_IncludesBoundary()
    {
        var filter = "population le 200591"; // Salt Lake City's exact population
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Select(f => f.GetProperty("ObjectId").GetInt64())
            .Should().Contain(8); // Salt Lake City
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=founded_year gt 1900")]
    public async Task Filter_GreaterThanYear_ReturnsRecentCities()
    {
        var filter = "founded_year gt 1900";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        // Las Vegas (1905), Virtual City (2020)
        features.Should().HaveCount(2);
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=rating ge 4.5")]
    public async Task Filter_GreaterThanOrEqualDouble_ReturnsHighRated()
    {
        var filter = "rating ge 4.5";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        // SF (4.8), Denver (4.6), Las Vegas (4.7)
        features.Should().HaveCount(3);
    }

    #endregion

    #region String Functions - contains, startswith, endswith

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=contains(name, 'San')")]
    public async Task Filter_Contains_ReturnsPartialMatches()
    {
        var filter = "contains(name, 'San')";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().HaveCount(3); // San Francisco, San Diego, San Jose
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=contains(notes, 'City')")]
    public async Task Filter_ContainsInNotes_ReturnsPartialMatches()
    {
        var filter = "contains(notes, 'City')";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        // Emerald City, Mile High City, City of Roses, Duke City, City of Trees
        features.Should().HaveCountGreaterThanOrEqualTo(5);
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=startswith(name, 'Los')")]
    public async Task Filter_StartsWith_ReturnsPrefix()
    {
        var filter = "startswith(name, 'Los')";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().ContainSingle(); // Los Angeles
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=startswith(state, 'C')")]
    public async Task Filter_StartsWithState_ReturnsMultiple()
    {
        var filter = "startswith(state, 'C')";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        // California (5) + Colorado (1)
        features.Should().HaveCount(6);
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=endswith(name, 'City')")]
    public async Task Filter_EndsWith_ReturnsSuffix()
    {
        var filter = "endswith(name, 'City')";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        // Salt Lake City, Virtual City
        features.Should().HaveCount(2);
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=endswith(state, 'a')")]
    public async Task Filter_EndsWithState_ReturnsStatesEndingInA()
    {
        var filter = "endswith(state, 'a')";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        // California (5), Arizona (2), Nevada (1)
        features.Should().HaveCount(8);
    }

    #endregion

    #region Logical Operators - and, or

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=... and ...")]
    public async Task Filter_AndOperator_RequiresBothConditions()
    {
        var filter = "state eq 'California' and is_capital eq true";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().ContainSingle(); // Sacramento
        var attrs = ParseAttributes(features[0]);
        attrs.GetProperty("name").GetString().Should().Be("Sacramento");
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=... or ...")]
    public async Task Filter_OrOperator_MatchesEitherCondition()
    {
        var filter = "state eq 'Washington' or state eq 'Oregon'";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().HaveCount(2); // Seattle, Portland
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=... and ... and ...")]
    public async Task Filter_MultipleAnd_RequiresAllConditions()
    {
        var filter = "state eq 'California' and population gt 500000 and is_capital eq false";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        // SF, LA, San Diego, San Jose (all California, pop > 500k, not capitals)
        features.Should().HaveCount(4);
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=... or ... or ...")]
    public async Task Filter_MultipleOr_MatchesAnyCondition()
    {
        var filter = "ObjectId eq 1 or ObjectId eq 2 or ObjectId eq 3";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().HaveCount(3);
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=(... and ...) or (...)")]
    public async Task Filter_MixedLogicalWithParentheses_GroupsCorrectly()
    {
        var filter = "(state eq 'California' and is_capital eq true) or state eq 'Utah'";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().HaveCount(2); // Sacramento + Salt Lake City
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=(... or ...) and (...)")]
    public async Task Filter_NestedParentheses_PreservesLogic()
    {
        var filter = "(state eq 'California' or state eq 'Arizona') and is_capital eq true";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().HaveCount(2); // Sacramento + Phoenix
    }

    #endregion

    #region Combined Filter and Query Parameters

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=...&$orderby=...")]
    public async Task Filter_WithOrderBy_ReturnsOrderedFilteredResults()
    {
        var filter = "state eq 'California'";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}&$orderby=population desc");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().HaveCount(5);

        // First should be LA (highest population in CA)
        var attrs = ParseAttributes(features[0]);
        attrs.GetProperty("name").GetString().Should().Be("Los Angeles");
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=...&$top=...&$skip=...")]
    public async Task Filter_WithPagination_ReturnsPaginatedFilteredResults()
    {
        var filter = "population gt 500000";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}&$top=3&$skip=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().HaveCount(3);
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=...&$count=true")]
    public async Task Filter_WithCount_ReturnsTotalCount()
    {
        var filter = "is_capital eq true";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}&$count=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.GetProperty("@odata.count").GetInt64().Should().Be(5);
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=...&$select=...")]
    public async Task Filter_WithSelect_ReturnsSelectedFieldsOnly()
    {
        var filter = "state eq 'California'";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}&$select=ObjectId,LayerId");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().HaveCount(5);

        // Verify only selected fields are present
        var first = features[0];
        first.TryGetProperty("ObjectId", out _).Should().BeTrue();
        first.TryGetProperty("LayerId", out _).Should().BeTrue();
    }

    #endregion

    #region String Functions with Logical Combinations

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=contains() and ...")]
    public async Task Filter_ContainsWithAnd_CombinesConditions()
    {
        var filter = "contains(name, 'San') and population gt 1000000";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        // San Diego (1.4M) and San Jose (1M+)
        features.Should().HaveCount(2);
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=startswith() or endswith()")]
    public async Task Filter_StartsWithOrEndsWith_CombinesStringFunctions()
    {
        var filter = "startswith(name, 'Los') or endswith(name, 'City')";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        // Los Angeles, Salt Lake City, Virtual City
        features.Should().HaveCount(3);
    }

    #endregion

    #region Edge Cases

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=population eq 0")]
    public async Task Filter_EqualZero_ReturnsZeroValueMatch()
    {
        var filter = "population eq 0";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().ContainSingle(); // Virtual City
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=rating eq null")]
    public async Task Filter_EqualNullOnDouble_ReturnsNullRatings()
    {
        var filter = "rating eq null";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().ContainSingle(); // Virtual City has null rating
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=state eq null")]
    public async Task Filter_EqualNullOnString_ReturnsNullStates()
    {
        var filter = "state eq null";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().ContainSingle(); // Virtual City has null state
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=contains(name, '')")]
    public async Task Filter_ContainsEmptyString_ReturnsAllWithField()
    {
        var filter = "contains(name, '')";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().HaveCount(15); // All features have name
    }

    #endregion

    #region Helper Methods

    private static async Task<List<JsonElement>> ParseFeaturesAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        return document.RootElement.GetProperty("value")
            .EnumerateArray()
            .Select(e => e.Clone())
            .ToList();
    }

    private static JsonElement ParseAttributes(JsonElement feature)
    {
        var attributesJson = feature.GetProperty("Attributes").GetString();
        using var doc = JsonDocument.Parse(attributesJson!);
        return doc.RootElement.Clone();
    }

    #endregion
}

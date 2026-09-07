// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Helpers;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.OData;

/// <summary>
/// Integration tests for OData $filter built-in functions that were previously untested:
/// - String functions: length, indexof, trim, replace, toupper
/// - Numeric functions: round, floor, ceiling, abs
/// - Temporal functions: month, day, hour, minute, second
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.ODataV4)]
public sealed class ODataFilterFunctionTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const int TestLayerId = 0;

    public async Task InitializeAsync()
    {
        // All segments are relative literal path fragments (not user input), so none can be
        // rooted and silently drop earlier arguments.
        _fixture.UseSeed(Path.Join("tests", "seed", "odata.yaml"));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    #region String Functions

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=length(...)")]
    public async Task Filter_LengthFunction_FiltersOnStringLength()
    {
        // "Boise" has length 5, "Portland" has length 8
        var filter = Uri.EscapeDataString("length(name) eq 5");
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={filter}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        // Boise is the only 5-character name in the 15-city seed (tests/seed/odata.yaml).
        // The per-row re-check below only rules out false positives; the id set is what
        // rules out false negatives, and it is the assertion an empty result fails.
        FeatureIds(features).Should().Equal(15L);
        foreach (var attrs in features.Select(ODataTestHelpers.ParseAttributes))
        {
            attrs.GetProperty("name").GetString()!.Length.Should().Be(5);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=length(...) gt")]
    public async Task Filter_LengthGreaterThan_FiltersLongNames()
    {
        // Filter for cities with names longer than 12 characters
        var filter = Uri.EscapeDataString("length(name) gt 12");
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={filter}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        // Only 'San Francisco' (13) and 'Salt Lake City' (14) exceed 12 characters.
        FeatureIds(features).Should().Equal(1L, 8L);
        foreach (var attrs in features.Select(ODataTestHelpers.ParseAttributes))
        {
            attrs.GetProperty("name").GetString()!.Length.Should().BeGreaterThan(12);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=indexof(...)")]
    public async Task Filter_IndexOfFunction_FindsSubstringPosition()
    {
        // indexof(name, 'San') returns 0 for "San Francisco", "San Diego", "San Jose"
        var filter = Uri.EscapeDataString("indexof(name,'San') eq 0");
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={filter}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        // San Francisco (1), San Diego (4) and San Jose (5) — and no other seeded city
        // name starts with 'San'.
        FeatureIds(features).Should().Equal(1L, 4L, 5L);
        foreach (var attrs in features.Select(ODataTestHelpers.ParseAttributes))
        {
            attrs.GetProperty("name").GetString().Should().StartWith("San");
        }
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=trim(...)")]
    public async Task Filter_TrimFunction_HandlesWhitespace()
    {
        // trim should not change results since seed data has no leading/trailing spaces
        // but the function should be accepted and not return an error
        var filter = Uri.EscapeDataString("trim(name) eq 'Denver'");
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={filter}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        FeatureIds(features).Should().Equal(9L);
        var attrs = ODataTestHelpers.ParseAttributes(features[0]);
        attrs.GetProperty("name").GetString().Should().Be("Denver");
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=replace(...)")]
    public async Task Filter_ReplaceFunction_ReplacesSubstring()
    {
        // replace(name, 'San ', '') eq 'Francisco' should match San Francisco
        var filter = Uri.EscapeDataString("replace(name,'San ','') eq 'Francisco'");
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={filter}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        FeatureIds(features).Should().Equal(1L);
        var attrs = ODataTestHelpers.ParseAttributes(features[0]);
        attrs.GetProperty("name").GetString().Should().Be("San Francisco");
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=toupper(...)")]
    public async Task Filter_ToUpperFunction_ComparesUpperCase()
    {
        var filter = Uri.EscapeDataString("toupper(state) eq 'CALIFORNIA'");
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={filter}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        // The five California cities are objectids 1-5; the remaining ten rows carry a
        // different state, or (Virtual City, 13) a null one.
        FeatureIds(features).Should().Equal(1L, 2L, 3L, 4L, 5L);
        foreach (var attrs in features.Select(ODataTestHelpers.ParseAttributes))
        {
            attrs.GetProperty("state").GetString().Should().Be("California");
        }
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=concat(...)")]
    public async Task Filter_ConcatFunction_ConcatenatesStrings()
    {
        var filter = Uri.EscapeDataString("concat(name,', USA') eq 'Denver, USA'");
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={filter}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        FeatureIds(features).Should().Equal(9L);
        var attrs = ODataTestHelpers.ParseAttributes(features[0]);
        attrs.GetProperty("name").GetString().Should().Be("Denver");
    }

    #endregion

    #region Numeric Functions

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=round(...)")]
    public async Task Filter_RoundFunction_RoundsToNearestInteger()
    {
        // rating values: 4.8, 4.2, 3.9, 4.5, 4.1, 4.4, 4.3, 3.8, 4.6, 4.0, 4.7, 3.7, null, 3.9, 4.2
        // round(4.8) = 5, round(4.2) = 4, etc.
        var filter = Uri.EscapeDataString("round(rating) eq 5");
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={filter}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        // `rating` is declared Double (tests/seed/odata.yaml), so the translator casts the
        // JSONB text to `double precision` and PostgreSQL's round(double precision) breaks
        // ties to even — San Diego's 4.5 rounds to 4, not 5. That leaves San Francisco
        // (4.8), Denver (4.6) and Las Vegas (4.7). Tucson/Salt Lake City/Sacramento sit
        // below 4.5 and Virtual City's rating is null.
        FeatureIds(features).Should().Equal(1L, 9L, 11L);
        foreach (var rating in features.Select(f => ODataTestHelpers.ParseAttributes(f).GetProperty("rating").GetDouble()))
        {
            Math.Round(rating, MidpointRounding.ToEven).Should().Be(5);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=floor(...)")]
    public async Task Filter_FloorFunction_RoundsDown()
    {
        // floor(4.8) = 4, floor(3.9) = 3
        var filter = Uri.EscapeDataString("floor(rating) eq 3");
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={filter}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        // Ratings in [3, 4): Sacramento 3.9, Salt Lake City 3.8, Tucson 3.7,
        // Albuquerque 3.9.
        FeatureIds(features).Should().Equal(3L, 8L, 12L, 14L);
        foreach (var rating in features.Select(f => ODataTestHelpers.ParseAttributes(f).GetProperty("rating").GetDouble()))
        {
            Math.Floor(rating).Should().Be(3);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=ceiling(...)")]
    public async Task Filter_CeilingFunction_RoundsUp()
    {
        // ceiling(4.2) = 5, ceiling(3.9) = 4
        var filter = Uri.EscapeDataString("ceiling(rating) eq 4");
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={filter}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        // Ratings in (3, 4]: Sacramento 3.9, Salt Lake City 3.8, Phoenix 4.0,
        // Tucson 3.7, Albuquerque 3.9.
        FeatureIds(features).Should().Equal(3L, 8L, 10L, 12L, 14L);
        foreach (var rating in features.Select(f => ODataTestHelpers.ParseAttributes(f).GetProperty("rating").GetDouble()))
        {
            Math.Ceiling(rating).Should().Be(4);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=abs(...)")]
    public async Task Filter_AbsFunction_ReturnsAbsoluteValue()
    {
        // All populations are positive, so abs(population) eq population
        // Use a specific check: abs(founded_year - 1800) lt 50 means founded between 1750-1850
        var filter = Uri.EscapeDataString("abs(rating) gt 4");
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={filter}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        // Ratings strictly greater than 4. Phoenix (exactly 4.0) and Virtual City (null)
        // are excluded, as are the four sub-4 ratings.
        FeatureIds(features).Should().Equal(1L, 2L, 4L, 5L, 6L, 7L, 9L, 11L, 15L);
        foreach (var rating in features.Select(f => ODataTestHelpers.ParseAttributes(f).GetProperty("rating").GetDouble()))
        {
            Math.Abs(rating).Should().BeGreaterThan(4);
        }
    }

    #endregion

    #region Temporal Functions

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=month(...)")]
    public async Task Filter_MonthFunction_FiltersByMonth()
    {
        // event_date '2024-01-01T00:00:00Z' has month=1 (San Francisco)
        // event_date '2024-02-15T12:30:00Z' has month=2 (Los Angeles)
        // event_date '2024-03-10T08:45:00Z' has month=3 (Sacramento)
        var filter = Uri.EscapeDataString("month(event_date) eq 1");
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={filter}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        // Only four seeded rows carry an event_date at all (objectids 1, 2, 3 and 5), so
        // an ignored temporal function would return all fifteen. Inspecting features[0]
        // alone passed whenever the expected row merely happened to sort first.
        FeatureIds(features).Should().Equal(1L);
        var attrs = ODataTestHelpers.ParseAttributes(features[0]);
        attrs.GetProperty("name").GetString().Should().Be("San Francisco");
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=day(...)")]
    public async Task Filter_DayFunction_FiltersByDayOfMonth()
    {
        // event_date '2024-02-15T12:30:00Z' has day=15 (Los Angeles)
        var filter = Uri.EscapeDataString("day(event_date) eq 15");
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={filter}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        // Only four seeded rows carry an event_date at all (objectids 1, 2, 3 and 5), so
        // an ignored temporal function would return all fifteen. Inspecting features[0]
        // alone passed whenever the expected row merely happened to sort first.
        FeatureIds(features).Should().Equal(2L);
        var attrs = ODataTestHelpers.ParseAttributes(features[0]);
        attrs.GetProperty("name").GetString().Should().Be("Los Angeles");
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=hour(...)")]
    public async Task Filter_HourFunction_FiltersByHour()
    {
        // event_date '2024-02-15T12:30:00Z' has hour=12 (Los Angeles)
        var filter = Uri.EscapeDataString("hour(event_date) eq 12");
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={filter}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        // Only four seeded rows carry an event_date at all (objectids 1, 2, 3 and 5), so
        // an ignored temporal function would return all fifteen. Inspecting features[0]
        // alone passed whenever the expected row merely happened to sort first.
        FeatureIds(features).Should().Equal(2L);
        var attrs = ODataTestHelpers.ParseAttributes(features[0]);
        attrs.GetProperty("name").GetString().Should().Be("Los Angeles");
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=minute(...)")]
    public async Task Filter_MinuteFunction_FiltersByMinute()
    {
        // event_date '2024-02-15T12:30:00Z' has minute=30 (Los Angeles)
        var filter = Uri.EscapeDataString("minute(event_date) eq 30");
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={filter}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        // Only four seeded rows carry an event_date at all (objectids 1, 2, 3 and 5), so
        // an ignored temporal function would return all fifteen. Inspecting features[0]
        // alone passed whenever the expected row merely happened to sort first.
        FeatureIds(features).Should().Equal(2L);
        var attrs = ODataTestHelpers.ParseAttributes(features[0]);
        attrs.GetProperty("name").GetString().Should().Be("Los Angeles");
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=second(...)")]
    public async Task Filter_SecondFunction_FiltersBySecond()
    {
        // event_date '2024-03-10T08:45:00Z' has second=0 (Sacramento)
        // event_date '2024-12-31T23:59:59Z' has second=59 (San Jose)
        var filter = Uri.EscapeDataString("second(event_date) eq 59");
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={filter}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        // Only four seeded rows carry an event_date at all (objectids 1, 2, 3 and 5), so
        // an ignored temporal function would return all fifteen. Inspecting features[0]
        // alone passed whenever the expected row merely happened to sort first.
        FeatureIds(features).Should().Equal(5L);
        var attrs = ODataTestHelpers.ParseAttributes(features[0]);
        attrs.GetProperty("name").GetString().Should().Be("San Jose");
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=month(...) combined")]
    public async Task Filter_MonthAndDayFunctions_CombinedFilter()
    {
        // month(event_date) eq 3 and day(event_date) eq 10 → Sacramento
        var filter = Uri.EscapeDataString("month(event_date) eq 3 and day(event_date) eq 10");
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={filter}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        FeatureIds(features).Should().Equal(3L);
        var attrs = ODataTestHelpers.ParseAttributes(features[0]);
        attrs.GetProperty("name").GetString().Should().Be("Sacramento");
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=hour(...) and minute(...)")]
    public async Task Filter_HourAndMinuteFunctions_CombinedFilter()
    {
        // hour=8 and minute=45 → Sacramento (2024-03-10T08:45:00Z)
        var filter = Uri.EscapeDataString("hour(event_date) eq 8 and minute(event_date) eq 45");
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={filter}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        FeatureIds(features).Should().Equal(3L);
        var attrs = ODataTestHelpers.ParseAttributes(features[0]);
        attrs.GetProperty("name").GetString().Should().Be("Sacramento");
    }

    #endregion

    #region Combined Functions

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=length+contains")]
    public async Task Filter_LengthAndContains_CombinedStringFunctions()
    {
        // Cities with name containing 'City' AND length > 10
        var filter = Uri.EscapeDataString("contains(name,'City') and length(name) gt 10");
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={filter}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        // 'Salt Lake City' (14) and 'Virtual City' (12) are the only names containing
        // 'City'; both are longer than 10 characters, so the conjunction keeps both. With
        // no id set this test also passed on an empty result.
        FeatureIds(features).Should().Equal(8L, 13L);
        foreach (var name in features.Select(f => ODataTestHelpers.ParseAttributes(f).GetProperty("name").GetString()!))
        {
            name.Should().Contain("City");
            name.Length.Should().BeGreaterThan(10);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=floor+gt")]
    public async Task Filter_FloorWithComparison_CombinesNumericAndLogical()
    {
        // floor(area_sq_km) gt 1000 → Los Angeles (1213.9) and Phoenix (1341.0)
        var filter = Uri.EscapeDataString("floor(area_sq_km) gt 1000");
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={filter}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        // Los Angeles (1213.9) and Phoenix (1341.0) are the only two seeded areas above
        // 1000 km²; the next largest is San Diego at 964.5.
        FeatureIds(features).Should().Equal(2L, 10L);
        foreach (var area in features.Select(f => ODataTestHelpers.ParseAttributes(f).GetProperty("area_sq_km").GetDouble()))
        {
            Math.Floor(area).Should().BeGreaterThan(1000);
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Object ids of the returned rows, ascending. The seed is a fixed 15-city table, so
    /// every filter in this file has an id set that can be worked out by reading
    /// <c>tests/seed/odata.yaml</c> — which is what makes these oracles reject a filter
    /// that returns too few rows as well as one that returns too many.
    /// </summary>
    private static long[] FeatureIds(IEnumerable<JsonElement> features)
        => features.Select(ReadObjectId).OrderBy(id => id).ToArray();

    private static long ReadObjectId(JsonElement feature)
    {
        if (feature.TryGetProperty("ObjectId", out var objectId) && objectId.ValueKind == JsonValueKind.Number)
        {
            return objectId.GetInt64();
        }

        return ODataTestHelpers.ParseAttributes(feature).GetProperty("objectid").GetInt64();
    }

    private static async Task<List<JsonElement>> ParseFeaturesAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        return document.RootElement.GetProperty("value")
            .EnumerateArray()
            .Select(e => e.Clone())
            .ToList();
    }

    #endregion
}

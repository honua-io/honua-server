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

    // Expected sets below are computed by hand from tests/seed/odata.yaml layer 0
    // (objectids 1-15). Name lengths and ratings, in seed order:
    //   1  San Francisco  13  4.8    9  Denver          6  4.6
    //   2  Los Angeles    11  4.2   10  Phoenix         7  4.0
    //   3  Sacramento     10  3.9   11  Las Vegas       9  4.7
    //   4  San Diego       9  4.5   12  Tucson          6  3.7
    //   5  San Jose        8  4.1   13  Virtual City   12  (null)
    //   6  Seattle         7  4.4   14  Albuquerque    11  3.9
    //   7  Portland        8  4.3   15  Boise           5  4.2
    //   8  Salt Lake City 14  3.8
    // Asserting the full expected set (not just a per-row re-check of the same
    // predicate) is what rules out false negatives — an over-restrictive filter
    // passed every one of these tests before (#4393).
    private static async Task<string[]> NamesAsync(HttpResponseMessage response)
    {
        var features = await ParseFeaturesAsync(response);
        return features
            .Select(feature => ODataTestHelpers.ParseAttributes(feature).GetProperty("name").GetString()!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<HttpResponseMessage> FilterAsync(string filter)
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}&$top=100");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return response;
    }

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
        // Boise is the only seeded city with a five-character name.
        using var response = await FilterAsync("length(name) eq 5");
        (await NamesAsync(response)).Should().Equal("Boise");
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=length(...) gt")]
    public async Task Filter_LengthGreaterThan_FiltersLongNames()
    {
        // Only San Francisco (13) and Salt Lake City (14) exceed 12 characters;
        // Virtual City is exactly 12 and must be excluded, which pins the boundary.
        using var response = await FilterAsync("length(name) gt 12");
        (await NamesAsync(response)).Should().Equal("Salt Lake City", "San Francisco");
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=indexof(...)")]
    public async Task Filter_IndexOfFunction_FindsSubstringPosition()
    {
        // Exactly the three cities whose name starts with "San".
        using var response = await FilterAsync("indexof(name,'San') eq 0");
        (await NamesAsync(response)).Should().Equal("San Diego", "San Francisco", "San Jose");
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
        features.Should().ContainSingle();
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
        features.Should().ContainSingle();
        var attrs = ODataTestHelpers.ParseAttributes(features[0]);
        attrs.GetProperty("name").GetString().Should().Be("San Francisco");
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=toupper(...)")]
    public async Task Filter_ToUpperFunction_ComparesUpperCase()
    {
        // Exactly the five Californian cities; HaveCountGreaterThan(1) allowed an
        // over-restrictive match of any two of them (#4393).
        using var response = await FilterAsync("toupper(state) eq 'CALIFORNIA'");
        (await NamesAsync(response)).Should().Equal(
            "Los Angeles", "Sacramento", "San Diego", "San Francisco", "San Jose");
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
        features.Should().ContainSingle();
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
        // Ratings strictly above the 4.5 midpoint round to 5 under either tie-breaking
        // rule: San Francisco (4.8), Las Vegas (4.7), Denver (4.6). Every rating below
        // 4.5 must be absent. San Diego is exactly 4.5, and the tie-break for a
        // floating-point round is platform-defined (PostgreSQL rounds numeric halves
        // away from zero but double-precision halves per the C library), so it is the
        // one row whose membership is not fixed by the seed. Bracketing the answer
        // between those two sets still rules out both false positives and the false
        // negatives the previous per-row re-check could not see (#4393).
        using var response = await FilterAsync("round(rating) eq 5");
        var names = await NamesAsync(response);

        names.Should().Contain(["Denver", "Las Vegas", "San Francisco"]);
        names.Should().BeSubsetOf(["Denver", "Las Vegas", "San Diego", "San Francisco"]);
        names.Should().OnlyHaveUniqueItems();

        // The rows the seed does fix exactly: everything at or below 4.4 is excluded.
        names.Should().NotContain(["Albuquerque", "Boise", "Los Angeles", "Phoenix", "Portland",
            "Sacramento", "Salt Lake City", "San Jose", "Seattle", "Tucson", "Virtual City"]);
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=floor(...)")]
    public async Task Filter_FloorFunction_RoundsDown()
    {
        // Ratings in [3, 4): Sacramento 3.9, Salt Lake City 3.8, Tucson 3.7,
        // Albuquerque 3.9. Phoenix is exactly 4.0 and must be excluded; Virtual City
        // has a null rating and must be excluded.
        using var response = await FilterAsync("floor(rating) eq 3");
        (await NamesAsync(response)).Should()
            .Equal("Albuquerque", "Sacramento", "Salt Lake City", "Tucson");
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=ceiling(...)")]
    public async Task Filter_CeilingFunction_RoundsUp()
    {
        // Ratings in (3, 4]: the four sub-4 cities plus Phoenix at exactly 4.0, which
        // floor placed in the 3-bucket and ceiling must place in the 4-bucket. The two
        // exact sets together partition the seed and pin the boundary behaviour.
        using var response = await FilterAsync("ceiling(rating) eq 4");
        (await NamesAsync(response)).Should()
            .Equal("Albuquerque", "Phoenix", "Sacramento", "Salt Lake City", "Tucson");
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=abs(...)")]
    public async Task Filter_AbsFunction_ReturnsAbsoluteValue()
    {
        // Every seeded rating is positive, so abs is the identity here and the answer
        // is exactly the nine cities rated strictly above 4.0. Phoenix (4.0) is the
        // boundary row and must be excluded; Virtual City has a null rating.
        using var response = await FilterAsync("abs(rating) gt 4");
        (await NamesAsync(response)).Should().Equal(
            "Boise", "Denver", "Las Vegas", "Los Angeles", "Portland",
            "San Diego", "San Francisco", "San Jose", "Seattle");
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
        // Exactly one seeded city matches; asserting features[0] alone left extra
        // matches (a partially applied temporal predicate) undetected (#4393).
        using var response = await FilterAsync("month(event_date) eq 1");
        (await NamesAsync(response)).Should().Equal("San Francisco");
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=day(...)")]
    public async Task Filter_DayFunction_FiltersByDayOfMonth()
    {
        // event_date '2024-02-15T12:30:00Z' has day=15 (Los Angeles)
        // Exactly one seeded city matches; asserting features[0] alone left extra
        // matches (a partially applied temporal predicate) undetected (#4393).
        using var response = await FilterAsync("day(event_date) eq 15");
        (await NamesAsync(response)).Should().Equal("Los Angeles");
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=hour(...)")]
    public async Task Filter_HourFunction_FiltersByHour()
    {
        // event_date '2024-02-15T12:30:00Z' has hour=12 (Los Angeles)
        // Exactly one seeded city matches; asserting features[0] alone left extra
        // matches (a partially applied temporal predicate) undetected (#4393).
        using var response = await FilterAsync("hour(event_date) eq 12");
        (await NamesAsync(response)).Should().Equal("Los Angeles");
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=minute(...)")]
    public async Task Filter_MinuteFunction_FiltersByMinute()
    {
        // event_date '2024-02-15T12:30:00Z' has minute=30 (Los Angeles)
        // Exactly one seeded city matches; asserting features[0] alone left extra
        // matches (a partially applied temporal predicate) undetected (#4393).
        using var response = await FilterAsync("minute(event_date) eq 30");
        (await NamesAsync(response)).Should().Equal("Los Angeles");
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=second(...)")]
    public async Task Filter_SecondFunction_FiltersBySecond()
    {
        // event_date '2024-03-10T08:45:00Z' has second=0 (Sacramento)
        // event_date '2024-12-31T23:59:59Z' has second=59 (San Jose)
        // Exactly one seeded city matches; asserting features[0] alone left extra
        // matches (a partially applied temporal predicate) undetected (#4393).
        using var response = await FilterAsync("second(event_date) eq 59");
        (await NamesAsync(response)).Should().Equal("San Jose");
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
        features.Should().ContainSingle();
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
        features.Should().ContainSingle();
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
        // Salt Lake City (14) and Virtual City (12) are the only names containing
        // "City", and both clear the length bound. The previous body had no
        // emptiness assertion at all, so it passed on zero features (#4393).
        using var response = await FilterAsync("contains(name,'City') and length(name) gt 10");
        (await NamesAsync(response)).Should().Equal("Salt Lake City", "Virtual City");
    }

    [IntegrationTest]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=floor+gt")]
    public async Task Filter_FloorWithComparison_CombinesNumericAndLogical()
    {
        // floor(area_sq_km) gt 1000 → Los Angeles (1213.9) and Phoenix (1341.0)
        // floor(1213.9) = 1213 and floor(1341.0) = 1341 clear the bound; the next
        // largest, San Diego at 964.5, does not.
        using var response = await FilterAsync("floor(area_sq_km) gt 1000");
        (await NamesAsync(response)).Should().Equal("Los Angeles", "Phoenix");
    }

    #endregion

    #region Helpers

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

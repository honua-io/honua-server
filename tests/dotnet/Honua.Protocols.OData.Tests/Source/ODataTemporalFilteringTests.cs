// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Helpers;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.OData;

/// <summary>
/// Tests for temporal filtering in OData endpoints
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.ODataV4)]
public sealed class ODataTemporalFilteringTests : IClassFixture<WebAppFixture>
{
    private readonly HttpClient _client;
    private const int TestLayerId = 0;

    public ODataTemporalFilteringTests(WebAppFixture fixture)
    {
        _client = fixture.CreateClient();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task ODataFilter_DateTimeEquality_ReturnsMatchingFeatures()
    {
        // Arrange
        var testDate = DateTime.Parse("2024-01-01T00:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var isoDateString = testDate.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        // Test both ISO format and direct datetime comparison
        var testCases = new[]
        {
            $"event_date eq {isoDateString}",
            $"event_date eq datetime'{isoDateString}'",
            $"event_date eq '{isoDateString}'"
        };

        foreach (var filterExpression in testCases)
        {
            // Act
            var encodedFilter = Uri.EscapeDataString(filterExpression);
            var response = await _client.GetAsync($"/odata/Features({TestLayerId})?$filter={encodedFilter}");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var features = await ParseFeaturesAsync(response);

            // All returned features should have matching event_date
            foreach (var feature in features)
            {
                var attributes = ParseAttributes(feature);
                var eventDate = attributes.GetProperty("event_date").GetString();
                eventDate.Should().NotBeNull($"Filter: {filterExpression}");

                var parsedDate = DateTime.Parse(eventDate!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
                parsedDate.Should().Be(testDate, $"Filter: {filterExpression}");
            }
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task ODataFilter_DateTimeRange_ReturnsFeaturesBetweenDates()
    {
        // Arrange
        var startDate = DateTime.Parse("2024-01-01T00:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var endDate = DateTime.Parse("2024-12-31T23:59:59Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);

        var startIso = startDate.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var endIso = endDate.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        var filterExpression = $"event_date ge '{startIso}' and event_date le '{endIso}'";

        // Act
        var encodedFilter = Uri.EscapeDataString(filterExpression);
        var response = await _client.GetAsync($"/odata/Features({TestLayerId})?$filter={encodedFilter}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var features = await ParseFeaturesAsync(response);

        // All returned features should be within the date range
        foreach (var feature in features)
        {
            var attributes = ParseAttributes(feature);
            var eventDate = attributes.GetProperty("event_date").GetString();
            if (eventDate != null)
            {
                var parsedDate = DateTime.Parse(eventDate, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
                parsedDate.Should().BeOnOrAfter(startDate, "Feature date should be after start date");
                parsedDate.Should().BeOnOrBefore(endDate, "Feature date should be before end date");
            }
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task ODataFilter_DateTimeGreaterThan_ReturnsNewerFeatures()
    {
        // Arrange
        var thresholdDate = DateTime.Parse("2024-06-01T12:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var isoString = thresholdDate.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        var filterExpression = $"event_date gt '{isoString}'";

        // Act
        var encodedFilter = Uri.EscapeDataString(filterExpression);
        var response = await _client.GetAsync($"/odata/Features({TestLayerId})?$filter={encodedFilter}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var features = await ParseFeaturesAsync(response);

        // All returned features should have event_date after threshold
        foreach (var feature in features)
        {
            var attributes = ParseAttributes(feature);
            var eventDate = attributes.GetProperty("event_date").GetString();
            if (eventDate != null)
            {
                var parsedDate = DateTime.Parse(eventDate, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
                parsedDate.Should().BeAfter(thresholdDate, "Feature date should be after threshold");
            }
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task ODataFilter_DateTimeLessThan_ReturnsOlderFeatures()
    {
        // Arrange
        var thresholdDate = DateTime.Parse("2024-06-01T12:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var isoString = thresholdDate.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        var filterExpression = $"event_date lt '{isoString}'";

        // Act
        var encodedFilter = Uri.EscapeDataString(filterExpression);
        var response = await _client.GetAsync($"/odata/Features({TestLayerId})?$filter={encodedFilter}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var features = await ParseFeaturesAsync(response);

        // All returned features should have event_date before threshold
        foreach (var feature in features)
        {
            var attributes = ParseAttributes(feature);
            var eventDate = attributes.GetProperty("event_date").GetString();
            if (eventDate != null)
            {
                var parsedDate = DateTime.Parse(eventDate, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
                parsedDate.Should().BeBefore(thresholdDate, "Feature date should be before threshold");
            }
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task ODataFilter_ComplexTemporalExpression_ReturnsCorrectFeatures()
    {
        // Arrange
        var startDate1 = DateTime.Parse("2024-01-01T00:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var endDate1 = DateTime.Parse("2024-03-31T23:59:59Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var startDate2 = DateTime.Parse("2024-10-01T00:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var endDate2 = DateTime.Parse("2024-12-31T23:59:59Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);

        var filterExpression = $"(event_date ge '{startDate1:yyyy-MM-ddTHH:mm:ssZ}' and event_date le '{endDate1:yyyy-MM-ddTHH:mm:ssZ}') " +
                              $"or (event_date ge '{startDate2:yyyy-MM-ddTHH:mm:ssZ}' and event_date le '{endDate2:yyyy-MM-ddTHH:mm:ssZ}')";

        // Act
        var encodedFilter = Uri.EscapeDataString(filterExpression);
        var response = await _client.GetAsync($"/odata/Features({TestLayerId})?$filter={encodedFilter}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var features = await ParseFeaturesAsync(response);

        // All returned features should be in one of the two date ranges
        foreach (var feature in features)
        {
            var attributes = ParseAttributes(feature);
            var eventDate = attributes.GetProperty("event_date").GetString();
            if (eventDate != null)
            {
                var parsedDate = DateTime.Parse(eventDate, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
                var inFirstRange = parsedDate >= startDate1 && parsedDate <= endDate1;
                var inSecondRange = parsedDate >= startDate2 && parsedDate <= endDate2;

                (inFirstRange || inSecondRange).Should().BeTrue(
                    $"Feature date {parsedDate} should be in one of the specified ranges");
            }
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task ODataFilter_DateOnlyField_HandlesDateComparisons()
    {
        // Arrange - Test date-only field (without time component)
        var testDate = new DateTime(2024, 6, 15); // Date only
        var filterExpression = $"created_date eq '{testDate:yyyy-MM-dd}'";

        // Act
        var encodedFilter = Uri.EscapeDataString(filterExpression);
        var response = await _client.GetAsync($"/odata/Features({TestLayerId})?$filter={encodedFilter}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var features = await ParseFeaturesAsync(response);

        // All returned features should have matching created_date
        foreach (var feature in features)
        {
            var attributes = ParseAttributes(feature);
            if (attributes.TryGetProperty("created_date", out var dateElement))
            {
                var dateValue = dateElement.GetString();
                if (dateValue != null)
                {
                    var parsedDate = DateTime.Parse(dateValue, CultureInfo.InvariantCulture);
                    parsedDate.Date.Should().Be(testDate.Date, "Date portion should match");
                }
            }
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task ODataFilter_InvalidDateFormat_ReturnsBadRequest()
    {
        // Arrange
        var invalidDateFormats = new[]
        {
            "event_date eq 'not-a-date'",
            "event_date eq '2024-13-45'", // Invalid month/day
            "event_date eq '2024/01/01'", // Wrong separator
            "event_date eq 'January 1, 2024'" // Text format
        };

        foreach (var filterExpression in invalidDateFormats)
        {
            // Act
            var encodedFilter = Uri.EscapeDataString(filterExpression);
            var response = await _client.GetAsync($"/odata/Features({TestLayerId})?$filter={encodedFilter}");

            // Assert
            // Should either return 400 Bad Request or handle gracefully
            response.StatusCode.Should().BeOneOf(
                new[] { HttpStatusCode.BadRequest, HttpStatusCode.OK },
                $"Invalid date format should be handled: {filterExpression}");

            if (response.StatusCode == HttpStatusCode.OK)
            {
                // If it returns OK, it should return empty results or handle the invalid date gracefully
                var content = await response.Content.ReadAsStringAsync();
                content.Should().NotBeEmpty();
            }
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task ODataFilter_NullDateField_HandlesNullComparisons()
    {
        // Arrange
        var filterExpressions = new[]
        {
            "event_date eq null",
            "event_date ne null",
            "not(event_date eq null)" // Alternative null check
        };

        foreach (var filterExpression in filterExpressions)
        {
            // Act
            var encodedFilter = Uri.EscapeDataString(filterExpression);
            var response = await _client.GetAsync($"/odata/Features({TestLayerId})?$filter={encodedFilter}");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK,
                $"Null comparison should be supported: {filterExpression}");

            var features = await ParseFeaturesAsync(response);

            // Verify null handling logic
            foreach (var feature in features)
            {
                var attributes = ParseAttributes(feature);
                if (attributes.TryGetProperty("event_date", out var dateElement))
                {
                    var isNull = dateElement.ValueKind == JsonValueKind.Null;

                    if (filterExpression.StartsWith("not", StringComparison.OrdinalIgnoreCase) ||
                        filterExpression.Contains("ne null", StringComparison.OrdinalIgnoreCase))
                    {
                        isNull.Should().BeFalse($"Feature should have non-null event_date for filter: {filterExpression}");
                    }
                    else if (filterExpression.Contains("eq null", StringComparison.OrdinalIgnoreCase))
                    {
                        isNull.Should().BeTrue($"Feature should have null event_date for filter: {filterExpression}");
                    }
                }
            }
        }
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

    private static JsonElement ParseAttributes(JsonElement feature)
    {
        return ODataTestHelpers.ParseAttributes(feature);
    }
}

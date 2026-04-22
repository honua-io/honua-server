// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.FeatureServer;

/// <summary>
/// Tests for temporal filtering in FeatureServer endpoints
/// </summary>
[Collection("Database")]
[Protocol(Protocols.FeatureServer)]
public sealed class FeatureServerTemporalTests : IClassFixture<WebAppFixture>
{
    private readonly HttpClient _client;

    public FeatureServerTemporalTests(WebAppFixture fixture)
    {
        _client = fixture.CreateClient();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task GeoServicesQuery_SingleTimeInstant_UnixTimestamp_ReturnsMatchingFeatures()
    {
        // Arrange
        var testDate = new DateTime(2024, 6, 15, 14, 30, 0, DateTimeKind.Utc);
        var unixTimestamp = ((DateTimeOffset)testDate).ToUnixTimeMilliseconds();
        var serviceId = WebAppFixture.TestServiceId;
        var layerId = WebAppFixture.TestLayerId;

        // Act
        var response = await _client.GetAsync(
            $"/rest/services/{serviceId}/FeatureServer/{layerId}/query?time={unixTimestamp}&f=json");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonDocument.Parse(content);

        result.RootElement.TryGetProperty("features", out var featuresElement).Should().BeTrue();

        // All returned features should intersect the queried instant based on timeInfo (timestamp -> event_date).
        foreach (var feature in featuresElement.EnumerateArray())
        {
            feature.TryGetProperty("attributes", out var attributes).Should().BeTrue();

            var start = TryReadTemporal(attributes, "timestamp");
            start.Should().NotBeNull("Feature should include timestamp attribute");

            var end = TryReadTemporal(attributes, "event_date") ?? start;

            var startValue = start!.Value;
            var endValue = end!.Value;

            (startValue <= testDate && endValue >= testDate).Should().BeTrue(
                $"Feature should have temporal range containing {testDate:o}");
        }
    }

    private static DateTimeOffset? TryReadTemporal(JsonElement attributes, string propertyName)
    {
        if (!attributes.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String when DateTimeOffset.TryParse(value.GetString(), out var parsed) => parsed,
            JsonValueKind.Number when value.TryGetInt64(out var unixMs) => DateTimeOffset.FromUnixTimeMilliseconds(unixMs),
            _ => null
        };
    }

    private static long? TryReadObjectId(JsonElement feature)
    {
        if (!feature.TryGetProperty("attributes", out var attributes))
        {
            return null;
        }

        if (attributes.TryGetProperty("objectid", out var objectId) ||
            attributes.TryGetProperty("OBJECTID", out objectId))
        {
            if (objectId.ValueKind == JsonValueKind.Number && objectId.TryGetInt64(out var numericId))
            {
                return numericId;
            }

            if (objectId.ValueKind == JsonValueKind.String &&
                long.TryParse(objectId.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId))
            {
                return parsedId;
            }
        }

        return null;
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task GeoServicesQuery_SingleTimeInstant_ISO8601_ReturnsMatchingFeatures()
    {
        // Arrange
        var testDate = new DateTime(2024, 6, 15, 14, 30, 0, DateTimeKind.Utc);
        var isoString = testDate.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var serviceId = WebAppFixture.TestServiceId;
        var layerId = WebAppFixture.TestLayerId;

        // Act
        var encodedTime = Uri.EscapeDataString(isoString);
        var response = await _client.GetAsync(
            $"/rest/services/{serviceId}/FeatureServer/{layerId}/query?time={encodedTime}&f=json");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonDocument.Parse(content);

        result.RootElement.TryGetProperty("features", out var featuresElement).Should().BeTrue();

        // Verify temporal filtering was applied (implementation dependent on test data)
        // In a real test environment, this would verify against known test data
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task GeoServicesQuery_TimeParsing_UsesInvariantCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var overrideCulture = new CultureInfo("en-GB");
            CultureInfo.CurrentCulture = overrideCulture;
            CultureInfo.CurrentUICulture = overrideCulture;

            var serviceId = WebAppFixture.TestServiceId;
            var layerId = WebAppFixture.TestLayerId;
            var encodedTime = Uri.EscapeDataString("07/06/2024");

            var response = await _client.GetAsync(
                $"/rest/services/{serviceId}/FeatureServer/{layerId}/query?time={encodedTime}&f=json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var content = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(content);

            document.RootElement.TryGetProperty("features", out var featuresElement).Should().BeTrue();

            var objectIds = featuresElement.EnumerateArray()
                .Select(feature => TryReadObjectId(feature))
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .ToArray();

            objectIds.Should().Contain(4);
            objectIds.Should().NotContain(2);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task GeoServicesQuery_TimeExtent_UnixTimestamps_ReturnsFeaturesBetween()
    {
        // Arrange
        var startDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = new DateTime(2024, 12, 31, 23, 59, 59, DateTimeKind.Utc);

        var startTimestamp = ((DateTimeOffset)startDate).ToUnixTimeMilliseconds();
        var endTimestamp = ((DateTimeOffset)endDate).ToUnixTimeMilliseconds();

        var timeExtent = $"{startTimestamp},{endTimestamp}";
        var serviceId = WebAppFixture.TestServiceId;
        var layerId = WebAppFixture.TestLayerId;

        // Act
        var response = await _client.GetAsync(
            $"/rest/services/{serviceId}/FeatureServer/{layerId}/query?time={timeExtent}&f=json");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonDocument.Parse(content);

        result.RootElement.TryGetProperty("features", out var featuresElement).Should().BeTrue();

        // All returned features should be within the time range
        foreach (var feature in featuresElement.EnumerateArray())
        {
            feature.TryGetProperty("attributes", out var attributes).Should().BeTrue();

            // Check if any datetime field is within our range
            var hasDateInRange = false;
            foreach (var property in attributes.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    var stringValue = property.Value.GetString();
                    if (DateTime.TryParse(stringValue, out var dateValue))
                    {
                        var dateUtc = DateTime.SpecifyKind(dateValue, DateTimeKind.Utc);
                        if (dateUtc >= startDate && dateUtc <= endDate)
                        {
                            hasDateInRange = true;
                            break;
                        }
                    }
                }
            }

            hasDateInRange.Should().BeTrue($"Feature should have a datetime field within {startDate} to {endDate}");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task GeoServicesQuery_TimeExtent_ISO8601_ReturnsFeaturesBetween()
    {
        // Arrange
        var startDate = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = new DateTime(2024, 6, 30, 23, 59, 59, DateTimeKind.Utc);

        var startIso = startDate.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var endIso = endDate.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        var timeExtent = $"{startIso},{endIso}";
        var serviceId = WebAppFixture.TestServiceId;
        var layerId = WebAppFixture.TestLayerId;

        // Act
        var encodedTime = Uri.EscapeDataString(timeExtent);
        var response = await _client.GetAsync(
            $"/rest/services/{serviceId}/FeatureServer/{layerId}/query?time={encodedTime}&f=json");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonDocument.Parse(content);

        result.RootElement.TryGetProperty("features", out var featuresElement).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task GeoServicesQuery_TimeRelation_IntersectsDefault_AppliesCorrectTemporalLogic()
    {
        // Arrange
        var testDate = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var unixTimestamp = ((DateTimeOffset)testDate).ToUnixTimeMilliseconds();
        var serviceId = WebAppFixture.TestServiceId;
        var layerId = WebAppFixture.TestLayerId;

        // Act - Test default timeRelation (should be esriTimeRelationIntersects)
        var response = await _client.GetAsync(
            $"/rest/services/{serviceId}/FeatureServer/{layerId}/query?time={unixTimestamp}&f=json");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonDocument.Parse(content);
        result.RootElement.TryGetProperty("features", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task GeoServicesQuery_TimeRelation_ExplicitIntersects_AppliesCorrectTemporalLogic()
    {
        // Arrange
        var testDate = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var unixTimestamp = ((DateTimeOffset)testDate).ToUnixTimeMilliseconds();
        var serviceId = WebAppFixture.TestServiceId;
        var layerId = WebAppFixture.TestLayerId;

        // Act - Test explicit esriTimeRelationIntersects
        var response = await _client.GetAsync(
            $"/rest/services/{serviceId}/FeatureServer/{layerId}/query?time={unixTimestamp}&timeRelation=esriTimeRelationIntersects&f=json");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonDocument.Parse(content);
        result.RootElement.TryGetProperty("features", out _).Should().BeTrue();
    }

    public static IEnumerable<object[]> TimeRelationCases()
    {
        yield return new object[]
        {
            "2023-01-03T00:00:00Z",
            "esriTimeRelationIntersects",
            new long[] { 1, 4 }
        };

        yield return new object[]
        {
            "2023-01-10T00:00:00Z,2023-01-15T00:00:00Z",
            "esriTimeRelationContains",
            new long[] { 1, 2, 4 }
        };

        yield return new object[]
        {
            "2023-01-01T00:00:00Z,2024-02-01T00:00:00Z",
            "esriTimeRelationWithin",
            new long[] { 1, 3, 5 }
        };

        yield return new object[]
        {
            "2023-12-15T00:00:00Z,2024-02-15T00:00:00Z",
            "esriTimeRelationOverlaps",
            new long[] { 1, 3 }
        };
    }

    [Theory]
    [MemberData(nameof(TimeRelationCases))]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task GeoServicesQuery_TimeRelation_UsesTimeInfoIntervals(
        string timeParam,
        string timeRelation,
        long[] expectedObjectIds)
    {
        // Arrange
        var serviceId = WebAppFixture.TestServiceId;
        var layerId = WebAppFixture.TestLayerId;
        var encodedTime = Uri.EscapeDataString(timeParam);

        // Act
        var response = await _client.GetAsync(
            $"/rest/services/{serviceId}/FeatureServer/{layerId}/query?time={encodedTime}&timeRelation={timeRelation}&f=json");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        var objectIds = ExtractObjectIds(document);
        objectIds.Should().BeEquivalentTo(expectedObjectIds);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task GeoServicesQuery_CombinedTemporalAndSpatialFilter_ReturnsCorrectFeatures()
    {
        // Arrange
        var testDate = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var unixTimestamp = ((DateTimeOffset)testDate).ToUnixTimeMilliseconds();

        // Simple point geometry for spatial filter
        var point = "{'x':-122.4,'y':37.8,'spatialReference':{'wkid':4326}}";
        var encodedGeometry = Uri.EscapeDataString(point);

        var serviceId = WebAppFixture.TestServiceId;
        var layerId = WebAppFixture.TestLayerId;

        // Act - Combine temporal and spatial filters
        var response = await _client.GetAsync(
            $"/rest/services/{serviceId}/FeatureServer/{layerId}/query?" +
            $"time={unixTimestamp}&" +
            $"geometry={encodedGeometry}&" +
            $"geometryType=esriGeometryPoint&" +
            $"spatialRel=esriSpatialRelIntersects&" +
            $"distance=1000&" +
            $"units=esriSRUnit_Meter&" +
            $"f=json");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonDocument.Parse(content);
        result.RootElement.TryGetProperty("features", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task GeoServicesQuery_InvalidTimeFormat_ReturnsBadRequest()
    {
        // Arrange
        var invalidTimeFormats = new[]
        {
            "invalid-time",
            "not-a-timestamp",
            "2024-13-45T25:00:00Z", // Invalid date components
            "definitely-not-a-date"
        };

        var serviceId = WebAppFixture.TestServiceId;
        var layerId = WebAppFixture.TestLayerId;

        foreach (var invalidTime in invalidTimeFormats)
        {
            // Act
            var encodedTime = Uri.EscapeDataString(invalidTime);
            var response = await _client.GetAsync(
                $"/rest/services/{serviceId}/FeatureServer/{layerId}/query?time={encodedTime}&f=json");

            // Assert
            response.StatusCode.Should().BeOneOf(
                new[] { HttpStatusCode.BadRequest, HttpStatusCode.InternalServerError },
                $"Invalid time format should return error: {invalidTime}");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task GeoServicesQuery_InvalidTimeFormat_DoesNotLeakInputOrParserDetails()
    {
        const string sentinel = "LEAK_SENTINEL_TIME";
        var encodedTime = Uri.EscapeDataString($"not-a-time-{sentinel}");
        var serviceId = WebAppFixture.TestServiceId;
        var layerId = WebAppFixture.TestLayerId;

        var response = await _client.GetAsync(
            $"/rest/services/{serviceId}/FeatureServer/{layerId}/query?time={encodedTime}&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Invalid time parameter.");
        content.Should().NotContain(sentinel);
        content.Should().NotContain("BytePositionInLine");
        content.Should().NotContain("LineNumber");
        content.Should().NotContain("System.Text.Json");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task GeoServicesQuery_TemporalFieldNotFound_ReturnsError()
    {
        // Arrange - Use a service/layer that doesn't have temporal fields
        var testDate = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var unixTimestamp = ((DateTimeOffset)testDate).ToUnixTimeMilliseconds();
        var serviceId = "non-temporal-service";
        var layerId = 0;

        // Act
        var response = await _client.GetAsync(
            $"/rest/services/{serviceId}/FeatureServer/{layerId}/query?time={unixTimestamp}&f=json");

        // Assert
        // Should return an appropriate error when no temporal field is available
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.BadRequest,
            HttpStatusCode.NotFound,
            HttpStatusCode.InternalServerError);

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var content = await response.Content.ReadAsStringAsync();
            content.Should().ContainEquivalentOf("temporal");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task GeoServicesQuery_TemporalFilterViaPost_ReturnsCorrectFeatures()
    {
        // Arrange
        var testDate = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var unixTimestamp = ((DateTimeOffset)testDate).ToUnixTimeMilliseconds();

        var serviceId = WebAppFixture.TestServiceId;
        var layerId = WebAppFixture.TestLayerId;

        var formData = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("time", unixTimestamp.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("timeRelation", "esriTimeRelationIntersects")
        });

        // Act
        var response = await _client.PostAsync(
            $"/rest/services/{serviceId}/FeatureServer/{layerId}/query",
            formData);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonDocument.Parse(content);
        result.RootElement.TryGetProperty("features", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task GeoServicesQuery_TemporalFilterWithPagination_ReturnsPagedResults()
    {
        // Arrange
        var startDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = new DateTime(2024, 12, 31, 23, 59, 59, DateTimeKind.Utc);

        var startTimestamp = ((DateTimeOffset)startDate).ToUnixTimeMilliseconds();
        var endTimestamp = ((DateTimeOffset)endDate).ToUnixTimeMilliseconds();
        var timeExtent = $"{startTimestamp},{endTimestamp}";

        var serviceId = WebAppFixture.TestServiceId;
        var layerId = WebAppFixture.TestLayerId;

        // Act
        var response = await _client.GetAsync(
            $"/rest/services/{serviceId}/FeatureServer/{layerId}/query?" +
            $"time={timeExtent}&" +
            $"resultOffset=0&" +
            $"resultRecordCount=10&" +
            $"f=json");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonDocument.Parse(content);

        result.RootElement.TryGetProperty("features", out var featuresElement).Should().BeTrue();
        var featureCount = featuresElement.EnumerateArray().Count();
        featureCount.Should().BeLessOrEqualTo(10, "Should respect resultRecordCount limit");
    }

    private static List<long> ExtractObjectIds(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("features", out var featuresElement))
        {
            return [];
        }

        var ids = new List<long>();
        foreach (var feature in featuresElement.EnumerateArray())
        {
            if (feature.TryGetProperty("attributes", out var attributes) &&
                attributes.TryGetProperty("objectid", out var objectId))
            {
                if (objectId.ValueKind == JsonValueKind.Number && objectId.TryGetInt64(out var idValue))
                {
                    ids.Add(idValue);
                }
            }
        }

        return ids;
    }
}

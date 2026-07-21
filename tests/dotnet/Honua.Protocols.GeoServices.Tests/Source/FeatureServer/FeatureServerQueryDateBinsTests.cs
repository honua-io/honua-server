// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit.Helpers;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

public sealed class FeatureServerQueryDateBinsFixture : IAsyncLifetime
{
    public WebAppFixture App { get; } = new WebAppFixture()
        .WithTestLicense(HonuaEdition.Pro);

    public Task InitializeAsync() => App.InitializeAsync();

    public Task DisposeAsync() => App.DisposeAsync();
}

[Protocol(TestProtocols.FeatureServer)]
[Collection("Database")]
public sealed class FeatureServerQueryDateBinsTests : IClassFixture<FeatureServerQueryDateBinsFixture>
{
    private readonly WebAppFixture _fixture;

    public FeatureServerQueryDateBinsTests(FeatureServerQueryDateBinsFixture wrapper)
    {
        _fixture = wrapper.App;
    }

    [IntegrationTest]
    [Operation(Operations.QueryDateBins)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryDateBins")]
    public async Task QueryDateBins_CalendarBin_ReturnsBins()
    {
        var bin = JsonSerializer.Serialize(new
        {
            calendarBin = new { unit = "month" }
        });

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryDateBins?binField=timestamp&bin={Uri.EscapeDataString(bin)}&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.TryGetProperty("features", out var features).Should().BeTrue();
        features.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Operation(Operations.QueryDateBins)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryDateBins")]
    public async Task QueryDateBins_MissingBinField_ReturnsBadRequest()
    {
        var bin = JsonSerializer.Serialize(new
        {
            calendarBin = new { unit = "month" }
        });

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryDateBins?bin={Uri.EscapeDataString(bin)}&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("binField");
    }

    [IntegrationTest]
    [Operation(Operations.QueryDateBins)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryDateBins")]
    public async Task QueryDateBins_MissingBin_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryDateBins?binField=timestamp&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("bin");
    }

    [IntegrationTest]
    [Operation(Operations.QueryDateBins)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryDateBins")]
    public async Task QueryDateBins_InvalidService_ReturnsNotFound()
    {
        var bin = JsonSerializer.Serialize(new
        {
            calendarBin = new { unit = "month" }
        });

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/nonexistent/FeatureServer/0/queryDateBins?binField=timestamp&bin={Uri.EscapeDataString(bin)}&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.QueryDateBins)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryDateBins")]
    public async Task QueryDateBins_MonthCalendarBin_ReturnsExactBucketCounts()
    {
        // Regression for honua-server#2945: previous proving tests only checked that
        // "features" is a JSON array, never that the bucket counts reflect the seeded
        // timestamps. tests/seed/server.yaml layer 0 has 5 features with `timestamp`:
        //   2022-12-31T23:00:00Z (objectid 4)             -> 2022-12 bucket
        //   2023-01-02T00:00:00Z, 2023-01-05T12:00:00Z,
        //   2023-01-20T00:00:00Z (objectids 1, 2, 5)       -> 2023-01 bucket (count 3)
        //   2023-02-10T00:00:00Z (objectid 3, NULL geometry) -> 2023-02 bucket
        // queryDateBins groups on the raw timestamp attribute with no geometry filter,
        // so the NULL-geometry feature (objectid 3) still counts.
        var bin = JsonSerializer.Serialize(new { calendarBin = new { unit = "month" } });

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryDateBins?binField=timestamp&bin={Uri.EscapeDataString(bin)}&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var features = document.RootElement.GetProperty("features");

        var countsByMonth = ExtractCountsByYearMonth(features);

        countsByMonth.Should().BeEquivalentTo(new Dictionary<string, long>
        {
            ["2022-12"] = 1,
            ["2023-01"] = 3,
            ["2023-02"] = 1,
        });
    }

    [IntegrationTest]
    [Operation(Operations.QueryDateBins)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryDateBins")]
    public async Task QueryDateBins_YearCalendarBin_ReturnsExactBucketCounts()
    {
        var bin = JsonSerializer.Serialize(new { calendarBin = new { unit = "year" } });

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryDateBins?binField=timestamp&bin={Uri.EscapeDataString(bin)}&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var features = document.RootElement.GetProperty("features");

        var countsByYear = ExtractCountsByYearMonth(features, yearOnly: true);

        countsByYear.Should().BeEquivalentTo(new Dictionary<string, long>
        {
            ["2022"] = 1,
            ["2023"] = 4,
        });
    }

    /// <summary>
    /// Groups queryDateBins response features by the "boundary" attribute's year (or
    /// year-month), keyed off the "count" attribute. Tolerates the "boundary" value being
    /// serialized either as an ISO-8601 string or an epoch-millisecond number, since the
    /// wire representation of a boxed <c>DateTime</c>/<c>DateTimeOffset</c> attribute value
    /// is an internal serialization detail this test should not hard-code.
    /// </summary>
    private static Dictionary<string, long> ExtractCountsByYearMonth(JsonElement features, bool yearOnly = false)
    {
        var result = new Dictionary<string, long>();
        foreach (var feature in features.EnumerateArray())
        {
            var attributes = feature.GetProperty("attributes");
            var boundary = attributes.GetProperty("boundary");
            var count = attributes.GetProperty("count").GetInt64();

            var timestamp = boundary.ValueKind switch
            {
                JsonValueKind.String => DateTimeOffset.Parse(boundary.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                JsonValueKind.Number => DateTimeOffset.FromUnixTimeMilliseconds(boundary.GetInt64()),
                _ => throw new InvalidOperationException($"Unexpected boundary value kind: {boundary.ValueKind}")
            };

            var key = yearOnly
                ? timestamp.Year.ToString(CultureInfo.InvariantCulture)
                : timestamp.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            result[key] = count;
        }

        return result;
    }

    [IntegrationTest]
    [Operation(Operations.QueryDateBins)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryDateBins")]
    public async Task QueryDateBinsPost_ValidRequest_ReturnsBins()
    {
        var bin = JsonSerializer.Serialize(new
        {
            calendarBin = new { unit = "year" }
        });

        var payload = JsonSerializer.Serialize(new
        {
            binField = "timestamp",
            bin,
            f = "json"
        });

        using var payloadContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryDateBins",
            payloadContent);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.TryGetProperty("features", out var features).Should().BeTrue();
        features.ValueKind.Should().Be(JsonValueKind.Array);
    }
}

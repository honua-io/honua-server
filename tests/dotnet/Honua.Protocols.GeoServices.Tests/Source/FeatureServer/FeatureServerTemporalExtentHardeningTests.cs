// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

// temporalExtent against PostGIS: exact values, interval layers, row-visibility filters, error paths and
// agreement with the OGC API Features collection extent for the same layer.
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerTemporalExtentHardeningTests : IAsyncLifetime
{
    private const string Earliest = "1901-02-03T04:05:06Z";
    private const string Latest = "2099-08-07T06:05:04Z";
    private static readonly string ExtentPath =
        $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/temporalExtent?f=json";

    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/temporalExtent")]
    public async Task TemporalExtent_RowsAtBothEnds_ReportsTheirExactInstants()
    {
        UseTimeFields(new MetadataV2ObjectTemporal { StartTimeField = "timestamp" });
        await InsertRowAsync("earliest", Earliest);
        await InsertRowAsync("latest", Latest);

        var extent = await GetExtentAsync();

        extent.Min.Should().Be(Instant(Earliest));
        extent.Max.Should().Be(Instant(Latest));
        extent.MinEpochMs.Should().Be(Instant(Earliest).ToUnixTimeMilliseconds());
        extent.MaxEpochMs.Should().Be(Instant(Latest).ToUnixTimeMilliseconds());
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/temporalExtent")]
    public async Task TemporalExtent_ConfiguredFieldDiffersInCase_ReadsTheSchemaField()
    {
        UseTimeFields(new MetadataV2ObjectTemporal { StartTimeField = "TIMESTAMP" });
        await InsertRowAsync("earliest", Earliest);
        await InsertRowAsync("latest", Latest);

        var extent = await GetExtentAsync();

        extent.StartTimeField.Should().Be("timestamp");
        extent.Min.Should().Be(Instant(Earliest));
        extent.Max.Should().Be(Instant(Latest));
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/temporalExtent")]
    public async Task TemporalExtent_IntervalLayer_TakesTheMaximumFromTheEndField()
    {
        UseTimeFields(new MetadataV2ObjectTemporal { StartTimeField = "timestamp", EndTimeField = "event_date" });
        await _fixture.Postgres.ExecuteAsync(
            "INSERT INTO features (layer_id, geometry, attributes) VALUES (0, NULL, jsonb_build_object(" +
            $"'name', 'interval', 'timestamp', '{Earliest}', 'event_date', '{Latest}'));",
            _fixture.CurrentSchema);

        var extent = await GetExtentAsync();

        extent.EndTimeField.Should().Be("event_date");
        extent.Min.Should().Be(Instant(Earliest));
        extent.Max.Should().Be(Instant(Latest));
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/temporalExtent")]
    public async Task TemporalExtent_RowHiddenByPermanentFilter_DoesNotLeakItsTime()
    {
        UseTimeFields(new MetadataV2ObjectTemporal { StartTimeField = "timestamp" });
        await InsertRowAsync("hidden-earliest", Earliest);
        (await GetExtentAsync()).Min.Should().Be(Instant(Earliest), "the row is visible before the filter");

        _fixture.UpdateV2ResourceMetadata(
            WebAppFixture.TestLayerId,
            permanentFilter: new MetadataV2PermanentFilter { Expression = "name NOT LIKE 'hidden%'" });

        (await GetExtentAsync()).Min.Should().BeAfter(Instant(Earliest));
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/temporalExtent")]
    public async Task TemporalExtent_StartFieldIsNotATimeField_ReturnsNotFound()
    {
        UseTimeFields(new MetadataV2ObjectTemporal { StartTimeField = "name" });

        using var response = await _fixture.Client.GetAsync(ExtentPath);

        await response.AssertGeoServicesErrorAsync(404);
    }

    [IntegrationTheory]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/temporalExtent")]
    [InlineData("missing-service/FeatureServer/0")]
    [InlineData("test/FeatureServer/2147483647")]
    public async Task TemporalExtent_UnknownIdentifiers_ReturnNotFound(string servicePath)
    {
        UseTimeFields(new MetadataV2ObjectTemporal { StartTimeField = "timestamp" });

        using var response = await _fixture.Client.GetAsync($"/rest/services/{servicePath}/temporalExtent?f=json");

        await response.AssertGeoServicesErrorAsync(404);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/temporalExtent")]
    [Endpoint("GET /ogc/features/collections/{collectionId}")]
    public async Task TemporalExtent_AgreesWithTheOgcFeaturesCollectionExtent()
    {
        UseTimeFields(new MetadataV2ObjectTemporal { StartTimeField = "timestamp" });
        await InsertRowAsync("earliest", Earliest);
        await InsertRowAsync("latest", Latest);

        var extent = await GetExtentAsync();
        using var collection = JsonDocument.Parse(await _fixture.Client.GetStringAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}?f=json"));
        var interval = collection.RootElement.GetProperty("extent").GetProperty("temporal").GetProperty("interval")[0];

        Instant(interval[0].GetString()!).Should().Be(extent.Min!.Value);
        Instant(interval[1].GetString()!).Should().Be(extent.Max!.Value);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/temporalExtent")]
    public async Task TemporalExtent_NotATemporalEndField_ReturnsNotFound()
    {
        UseTimeFields(new MetadataV2ObjectTemporal { StartTimeField = "timestamp", EndTimeField = "name" });

        using var response = await _fixture.Client.GetAsync(ExtentPath);

        await response.AssertGeoServicesErrorAsync(404);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/temporalExtent")]
    public async Task TemporalExtent_NoVisibleRows_ReturnsNullBounds()
    {
        UseTimeFields(new MetadataV2ObjectTemporal { StartTimeField = "timestamp" });
        (await GetExtentAsync()).Min.Should().NotBeNull();

        _fixture.UpdateV2ResourceMetadata(
            WebAppFixture.TestLayerId,
            permanentFilter: new MetadataV2PermanentFilter { Expression = "name = 'no-matching-feature'" });

        var extent = await GetExtentAsync();

        extent.StartTimeField.Should().Be("timestamp");
        extent.Min.Should().BeNull();
        extent.Max.Should().BeNull();
        extent.MinEpochMs.Should().BeNull();
        extent.MaxEpochMs.Should().BeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/temporalExtent")]
    public async Task TemporalExtent_StartAndEndUseSameField_ReportsExactBounds()
    {
        UseTimeFields(new MetadataV2ObjectTemporal { StartTimeField = "timestamp", EndTimeField = "timestamp" });
        await InsertRowAsync("earliest", Earliest);
        await InsertRowAsync("latest", Latest);

        var extent = await GetExtentAsync();

        extent.EndTimeField.Should().Be("timestamp");
        extent.Min.Should().Be(Instant(Earliest));
        extent.Max.Should().Be(Instant(Latest));
    }

    private void UseTimeFields(MetadataV2ObjectTemporal temporal)
        => _fixture.UpdateV2ResourceMetadata(WebAppFixture.TestLayerId, temporal: temporal);

    private Task InsertRowAsync(string name, string timestamp)
        => _fixture.Postgres.ExecuteAsync(
            "INSERT INTO features (layer_id, geometry, attributes) VALUES " +
            $"(0, NULL, jsonb_build_object('name', '{name}', 'timestamp', '{timestamp}'));",
            _fixture.CurrentSchema);

    private async Task<ExtentResponse> GetExtentAsync()
    {
        using var response = await _fixture.Client.GetAsync(ExtentPath);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.TryGetProperty("error", out _).Should().BeFalse(root.GetRawText());
        return new ExtentResponse
        {
            StartTimeField = OptionalString(root, "startTimeField"),
            EndTimeField = OptionalString(root, "endTimeField"),
            Min = OptionalString(root, "min") is { } min ? Instant(min) : null,
            Max = OptionalString(root, "max") is { } max ? Instant(max) : null,
            MinEpochMs = root.TryGetProperty("minEpochMs", out var minMs) && minMs.ValueKind == JsonValueKind.Number ? minMs.GetInt64() : null,
            MaxEpochMs = root.TryGetProperty("maxEpochMs", out var maxMs) && maxMs.ValueKind == JsonValueKind.Number ? maxMs.GetInt64() : null,
        };
    }

    private static string? OptionalString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset Instant(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private sealed record ExtentResponse
    {
        public string? StartTimeField { get; init; }

        public string? EndTimeField { get; init; }

        public DateTimeOffset? Min { get; init; }

        public DateTimeOffset? Max { get; init; }

        public long? MinEpochMs { get; init; }

        public long? MaxEpochMs { get; init; }
    }
}

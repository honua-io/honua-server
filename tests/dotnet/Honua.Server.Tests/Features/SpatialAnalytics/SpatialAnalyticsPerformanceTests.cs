// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
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
using Npgsql;

namespace Honua.Server.Tests.Features.SpatialAnalytics;

/// <summary>
/// Performance benchmarks for the Pro-tier spatial analytics endpoints. The
/// ticket #342 acceptance criterion requires DBSCAN over 100,000 features to
/// complete in under 5 seconds end-to-end on the reference PostGIS image used
/// by the integration test harness. These tests bulk-load synthetic points
/// into the canonical <c>features</c> table under the test schema so the
/// measurement reflects the full HTTP → handler → SQL builder → PostGIS path.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Performance")]
[Collection("Database")]
[Protocol(TestProtocols.SpatialAnalytics)]
public sealed class SpatialAnalyticsPerformanceTests : IAsyncLifetime
{
    private const int SyntheticFeatureCount = 100_000;
    private static readonly TimeSpan DbscanBudget = TimeSpan.FromSeconds(5);

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .WithTestLicense(HonuaEdition.Pro);

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    /// <summary>
    /// Seeds <see cref="SyntheticFeatureCount"/> synthetic point features into
    /// the test schema's <c>features</c> table tagged with the shared test
    /// layer id, then verifies that DBSCAN clustering via the REST endpoint
    /// completes within the 5-second budget. The synthetic points are
    /// deterministically scattered across a ~1° x 1° bounding box so the
    /// workload is stable across runs while still producing a realistic mix
    /// of noise and clusters.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.QueryClusters)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryClusters")]
    public async Task QueryClusters_Dbscan_100K_CompletesWithinBudget()
    {
        // Arrange — bulk-load 100K synthetic points into the test schema.
        await SeedSyntheticFeaturesAsync(SyntheticFeatureCount);

        var payload = JsonSerializer.Serialize(new
        {
            algorithm = "dbscan",
            eps = 1000,
            minPoints = 5,
            f = "json"
        });

        // Act — hit the REST endpoint and measure the full HTTP round-trip.
        var stopwatch = Stopwatch.StartNew();
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryClusters",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        var content = await response.Content.ReadAsStringAsync();
        stopwatch.Stop();

        // Assert — 200 OK, valid payload, within the 5s budget.
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"DBSCAN request should succeed. Response body: {content}");

        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;
        root.GetProperty("type").GetString().Should().Be("FeatureCollection");
        root.GetProperty("metadata").GetProperty("operation").GetString().Should().Be("cluster");

        // The result should contain at least the seeded features (per-feature mode),
        // bounded by MaxInputFeatures (100K by default). Allow up to the cap.
        var featureCount = root.GetProperty("features").GetArrayLength();
        featureCount.Should().BeGreaterThan(0, "DBSCAN should return assigned rows for the seeded points.");

        stopwatch.Elapsed.Should().BeLessThan(DbscanBudget,
            $"DBSCAN over {SyntheticFeatureCount:N0} features must complete within {DbscanBudget.TotalSeconds}s " +
            $"(actual: {stopwatch.Elapsed.TotalMilliseconds:F0}ms).");
    }

    private async Task SeedSyntheticFeaturesAsync(int count)
    {
        if (string.IsNullOrWhiteSpace(_fixture.CurrentSchema))
        {
            throw new InvalidOperationException("Test schema not initialized.");
        }

        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);

        // Count current layer-0 rows — the default seed inserts a handful of features.
        await using var countCmd = connection.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM features WHERE layer_id = @layerId;";
        countCmd.Parameters.Add(new NpgsqlParameter { ParameterName = "@layerId", Value = WebAppFixture.TestLayerId });
        var existing = Convert.ToInt32(await countCmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

        if (existing >= count)
        {
            return;
        }

        var toInsert = count - existing;

        // Bulk insert via a single generate_series statement — much faster than a
        // client-side loop and gives PostGIS a deterministic, reproducible layout.
        // Points are scattered across a 1° x 1° box near the equator using a cheap
        // hash on the sequence index so we avoid clustering every run on random().
        await using var insertCmd = connection.CreateCommand();
        insertCmd.CommandTimeout = 120;
        // generate_series returns int4, but the Knuth multiplicative hash
        // constant (2654435761) overflows int4 during multiplication. Cast i
        // to bigint so the multiply happens in int8 and the modulo stays in
        // the 0..999999 range before we scale into a double precision offset.
        insertCmd.CommandText = """
            INSERT INTO features (layer_id, geometry, attributes)
            SELECT
                @layerId,
                ST_SetSRID(ST_MakePoint(
                    -100.0 + (((i::bigint * 2654435761) % 1000000)::double precision / 1000000.0),
                    40.0 + (((i::bigint * 40503) % 1000000)::double precision / 1000000.0)
                ), 4326),
                jsonb_build_object('name', 'perf-' || i, 'bucket', (i % 50))
            FROM generate_series(1, @count) AS g(i);
            """;
        insertCmd.Parameters.Add(new NpgsqlParameter { ParameterName = "@layerId", Value = WebAppFixture.TestLayerId });
        insertCmd.Parameters.Add(new NpgsqlParameter { ParameterName = "@count", Value = toInsert });
        await insertCmd.ExecuteNonQueryAsync();

        // ANALYZE so the query planner has accurate row statistics for the
        // DBSCAN CTE. Without this the planner assumes the default row
        // estimate and can pick a non-indexed plan.
        await using var analyzeCmd = connection.CreateCommand();
        analyzeCmd.CommandText = "ANALYZE features;";
        await analyzeCmd.ExecuteNonQueryAsync();
    }
}

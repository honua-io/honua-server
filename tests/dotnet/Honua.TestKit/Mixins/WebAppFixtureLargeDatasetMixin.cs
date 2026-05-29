// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Npgsql;

namespace Honua.TestKit.Mixins;

/// <summary>
/// Audit-A2 mixin: the large-dataset seeding helper that <see cref="WebAppFixture"/>
/// historically inlined as ~55 LOC of <c>EnsureLargeTestDatasetAsync</c> plus the WKB
/// point-generation helper. Used by streaming-performance tests that need 2000+ feature
/// rows; entirely independent of the fixture's HTTP client, V2 graph, and schema
/// wiring, so it lives here as static helpers that take the
/// <see cref="PostgresFixture"/> + schema name + layer id.
/// </summary>
/// <remarks>
/// Per structural-audit-2026-05 (group A2), this is the fifth slice of the WebAppFixture
/// mixin split. Behaviour is byte-identical to the previous inline implementation —
/// pure relocation.
/// </remarks>
internal static class WebAppFixtureLargeDatasetMixin
{
    private const int TargetFeatureCount = 2000;

    /// <summary>
    /// Ensures the supplied schema contains at least <see cref="TargetFeatureCount"/>
    /// feature rows for the supplied layer id. Existing rows count toward the target so
    /// repeat invocations are idempotent. Mirrors the historic behaviour of
    /// <see cref="WebAppFixture.EnsureLargeTestDatasetAsync"/>.
    /// </summary>
    internal static async Task EnsureLargeTestDatasetAsync(
        PostgresFixture postgres,
        string schemaName,
        int layerId)
    {
        if (string.IsNullOrWhiteSpace(schemaName))
        {
            throw new InvalidOperationException("Test schema not initialized.");
        }

        await using var connection = await postgres.GetConnectionAsync(schemaName);

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM features WHERE layer_id = @layerId;";
        countCommand.Parameters.Add(new NpgsqlParameter { ParameterName = "@layerId", Value = layerId });
        var existingCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

        if (existingCount >= TargetFeatureCount)
        {
            return;
        }

        var additionalFeaturesNeeded = TargetFeatureCount - existingCount;

        await using var transaction = await connection.BeginTransactionAsync();
        await using var insertCommand = connection.CreateCommand();
        insertCommand.Transaction = transaction;
        insertCommand.CommandText = """
            INSERT INTO features (layer_id, geometry, attributes)
            VALUES (@layerId, ST_GeomFromWKB(@geometry, 4326), @attributes::jsonb);
            """;
        insertCommand.Parameters.Add(new NpgsqlParameter { ParameterName = "@layerId", Value = layerId });
        var geometryParam = new NpgsqlParameter { ParameterName = "@geometry", Value = DBNull.Value };
        var attributesParam = new NpgsqlParameter { ParameterName = "@attributes", Value = DBNull.Value };
        insertCommand.Parameters.Add(geometryParam);
        insertCommand.Parameters.Add(attributesParam);

        for (var i = 0; i < additionalFeaturesNeeded; i++)
        {
            geometryParam.Value = CreateTestPointGeometry(-180 + (i % 360), -90 + ((i / 360) % 180));

            var attributes = new Dictionary<string, object?>
            {
                ["Name"] = $"Streaming Test Feature {i}",
                ["Category"] = "Performance Test",
                ["Value"] = i % 100,
                ["TestBatch"] = "LargeDataset",
            };
            attributesParam.Value = JsonSerializer.Serialize(attributes);

            await insertCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    /// <summary>
    /// Creates a simple test point geometry as WKB. Mirrors the historic
    /// <c>CreateTestPointGeometry</c> private helper.
    /// </summary>
    private static byte[] CreateTestPointGeometry(double x, double y)
    {
        var geometryFactory = new GeometryFactory();
        var point = geometryFactory.CreatePoint(new Coordinate(x, y));
        var writer = new WKBWriter();
        return writer.Write(point);
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Npgsql;

namespace Honua.TestKit.Mixins;

internal static class WebAppFixtureLargeDatasetMixin
{
    private const int TargetFeatureCount = 2000;

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

    private static byte[] CreateTestPointGeometry(double x, double y)
    {
        var geometryFactory = new GeometryFactory();
        var point = geometryFactory.CreatePoint(new Coordinate(x, y));
        var writer = new WKBWriter();
        return writer.Write(point);
    }
}

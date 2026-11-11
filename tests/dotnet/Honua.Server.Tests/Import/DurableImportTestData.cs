// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.FileImport.Services;
using Honua.TestKit;
using Npgsql;

namespace Honua.Server.Tests.Import;

internal static class DurableImportTestData
{
    public static byte[] PointFeatureCollection(int featureCount)
    {
        var builder = new StringBuilder("""{"type":"FeatureCollection","features":[""");
        for (var i = 0; i < featureCount; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            var x = (-122 + (i * 0.001)).ToString(CultureInfo.InvariantCulture);
            var y = (37 + (i * 0.001)).ToString(CultureInfo.InvariantCulture);
            var n = i.ToString(CultureInfo.InvariantCulture);
            builder.Append("{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[")
                .Append(x).Append(',').Append(y)
                .Append("]},\"properties\":{\"n\":").Append(n).Append("}}");
        }

        builder.Append("]}");
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public static async Task<long> CountImportedRowsAsync(WebAppFixture fixture, string tableName)
    {
        await using var connection = new NpgsqlConnection(fixture.DatabaseConnectionProvider.GetConnectionString());
        await connection.OpenAsync();
        await using var lookup = new NpgsqlCommand(
            "SELECT table_schema, table_name FROM information_schema.tables WHERE table_name = @name LIMIT 1",
            connection);
        lookup.Parameters.AddWithValue("name", ImportedSnapshotRefresher.ResolvePhysicalImportedTableNames(tableName)[^1]);
        string schema;
        string physical;
        await using (var reader = await lookup.ExecuteReaderAsync())
        {
            (await reader.ReadAsync()).Should().BeTrue("the import created its target table");
            schema = reader.GetString(0);
            physical = reader.GetString(1);
        }

        await using var count = new NpgsqlCommand($"SELECT count(*) FROM \"{schema}\".\"{physical}\"", connection);
        return Convert.ToInt64(await count.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
}

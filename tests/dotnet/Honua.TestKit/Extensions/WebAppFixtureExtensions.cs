// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.TestKit.Extensions;

public static class WebAppFixtureExtensions
{
    public static async Task<long> InsertFeatureAsync(
        this WebAppFixture fixture,
        int layerId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var schema = fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        await using var connection = await fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO features (layer_id, geometry, attributes)
            VALUES (@layerId, NULL, jsonb_build_object('name', @name))
            RETURNING objectid;
            """;
        command.Parameters.AddWithValue("layerId", layerId);
        command.Parameters.AddWithValue("name", name);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }
}

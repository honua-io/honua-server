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

    /// <summary>
    /// Reads the stored <c>name</c> attribute straight out of Postgres, bypassing every server
    /// read path (and therefore every cache, projection or substituted reader), so a test can
    /// assert what the database actually holds after an edit. Returns <see langword="null"/> when
    /// the row does not exist.
    /// </summary>
    public static async Task<string?> ReadStoredFeatureNameAsync(
        this WebAppFixture fixture,
        int layerId,
        long objectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var schema = fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        await using var connection = await fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT attributes->>'name' FROM features WHERE layer_id = @layerId AND objectid = @objectId";
        command.Parameters.AddWithValue("layerId", layerId);
        command.Parameters.AddWithValue("objectId", objectId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    /// <summary>
    /// Counts stored rows whose <c>name</c> attribute matches, straight out of Postgres. The
    /// at-most-once edit proofs need "how many rows exist", which a response envelope cannot show:
    /// a deduplicated retry and a duplicated insert can return the same body.
    /// </summary>
    public static async Task<long> CountStoredFeaturesByNameAsync(
        this WebAppFixture fixture,
        int layerId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var schema = fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        await using var connection = await fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM features WHERE layer_id = @layerId AND attributes->>'name' = @name";
        command.Parameters.AddWithValue("layerId", layerId);
        command.Parameters.AddWithValue("name", name);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Counts every stored row on a layer, straight out of Postgres.
    /// </summary>
    public static async Task<long> CountStoredFeaturesAsync(
        this WebAppFixture fixture,
        int layerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var schema = fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        await using var connection = await fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM features WHERE layer_id = @layerId";
        command.Parameters.AddWithValue("layerId", layerId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Rewrites a stored row's <c>name</c> attribute directly in Postgres, standing in for a
    /// concurrent writer that committed outside the request under test. Returns the number of rows
    /// affected so a caller can assert the interfering write actually landed.
    /// </summary>
    public static async Task<int> UpdateStoredFeatureNameAsync(
        this WebAppFixture fixture,
        int layerId,
        long objectId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var schema = fixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        await using var connection = await fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE features
            SET attributes = jsonb_set(attributes, '{name}', to_jsonb(@name::text))
            WHERE layer_id = @layerId AND objectid = @objectId;
            """;
        command.Parameters.AddWithValue("layerId", layerId);
        command.Parameters.AddWithValue("objectId", objectId);
        command.Parameters.AddWithValue("name", name);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

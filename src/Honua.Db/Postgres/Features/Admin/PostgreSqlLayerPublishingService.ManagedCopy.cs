// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.Admin;

internal sealed partial class PostgreSqlLayerPublishingService
{
    private async Task<string> ValidateManagedCopyTargetAsync(
        string connectionString,
        IReadOnlyList<LayerFieldInsert> fields,
        CancellationToken cancellationToken)
    {
        if (fields.Any(field => field.Name.Equals(ManagedSourceIdField, StringComparison.OrdinalIgnoreCase)))
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation,
                $"The field '{ManagedSourceIdField}' is reserved for managed-copy identity mapping.");
        }
        if (_managedConnectionProvider is null)
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation,
                "The managed feature store is not configured for editable publication.");
        }

        await using var managed = await _managedConnectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var source = new NpgsqlConnection(connectionString);
        await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var target = managed.Connection;
        if (!string.Equals(source.Host, target.Host, StringComparison.OrdinalIgnoreCase) ||
            source.Port != target.Port || !string.Equals(source.Database, target.Database, StringComparison.Ordinal))
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation,
                "An editable copy must use the configured managed database host, port and database. Import remote data into that database first.");
        }

        var sourceStore = await ReadManagedStoreIdentityAsync(source, "features", cancellationToken).ConfigureAwait(false);
        var targetStore = await ReadManagedStoreIdentityAsync(target, _managedFeaturesTable, cancellationToken).ConfigureAwait(false);
        if (sourceStore != targetStore)
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation,
                "The publication connection must resolve the same features table as the managed writer. Check its search path.");
        }
        return targetStore.Schema;
    }

    private static async Task<(string Schema, long ObjectId)> ReadManagedStoreIdentityAsync(
        NpgsqlConnection connection, string tableName, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT namespace.nspname, relation.oid::bigint
            FROM pg_class AS relation
            JOIN pg_namespace AS namespace ON namespace.oid = relation.relnamespace
            WHERE relation.oid = to_regclass(@table);
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@table", tableName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation,
                "The managed features table is unavailable on the connection search path.");
        }
        return (reader.GetString(0), reader.GetInt64(1));
    }

    private static async Task PrepareManagedCopyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int layerId,
        string managedSchema,
        string sourceIdField,
        int srid,
        CancellationToken cancellationToken)
    {
        // Preserve the source identity separately, then make the public primary-ID
        // attribute agree with the managed writer's physical objectid. This is a new
        // publication; attachments and relationships must use this explicit mapping.
        var featuresTable = $"{QuoteIdentifier(managedSchema)}.\"features\"";
        var sql = $$"""
            UPDATE {{featuresTable}}
            SET attributes = COALESCE(attributes, '{}'::jsonb)
                || jsonb_build_object(@sourceIdentity::text, attributes->@primaryKey::text, @primaryKey::text, objectid)
            WHERE layer_id = @layerId;

            UPDATE honua.layers
            SET table_schema = @schema, table_name = 'features',
                primary_key_column = 'objectid', geometry_column = 'geometry',
                storage_srid = @srid,
                storage_options = '{"sourceBacked":"false","managedCopy":"true","managedStore":"true","attributesColumn":"attributes","layerDiscriminatorColumn":"layer_id"}'::jsonb
            WHERE layer_id = @layerId;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@layerId", layerId);
        command.Parameters.AddWithValue("@schema", managedSchema);
        command.Parameters.AddWithValue("@srid", srid);
        command.Parameters.AddWithValue("@sourceIdentity", ManagedSourceIdField);
        command.Parameters.AddWithValue("@primaryKey", sourceIdField);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

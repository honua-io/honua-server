// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Npgsql;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed partial class PostgresStorageMappedFeatureReader
{
    private const string ManagedGlobalIdIndexSql = """
        SELECT EXISTS (
            SELECT 1 FROM pg_index identity_index
            JOIN pg_class index_relation ON index_relation.oid = identity_index.indexrelid
            WHERE identity_index.indrelid = to_regclass(@table)
                AND index_relation.relname = 'idx_features_gid_' || md5(@layer::text || ':' || @field)
                AND identity_index.indisunique AND identity_index.indisvalid AND identity_index.indisready
                AND NOT identity_index.indnullsnotdistinct
                AND identity_index.indnkeyatts = 1 AND identity_index.indnatts = 1
                AND pg_get_expr(identity_index.indexprs, identity_index.indrelid)
                    = format('(NULLIF((attributes ->> %L::text), %L::text))::uuid', @field, '')
                AND pg_get_expr(identity_index.indpred, identity_index.indrelid) = format('(layer_id = %s)', @layer)
        )
        """;

    private async Task<bool> HasManagedGlobalIdIndexAsync(int storageLayerId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_resource.Editing?.GlobalIdField))
        {
            return true;
        }
        var field = _resource.SchemaFields.FirstOrDefault(candidate =>
            candidate.Name.Equals(_resource.Editing.GlobalIdField, StringComparison.OrdinalIgnoreCase));
        if (field is null)
        {
            return false;
        }
        try
        {
            await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = ManagedGlobalIdIndexSql;
            command.Parameters.Add(new NpgsqlParameter("table", _qualifiedTableName));
            command.Parameters.Add(new NpgsqlParameter("layer", storageLayerId));
            command.Parameters.Add(new NpgsqlParameter("field", field.Name));
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
        }
        catch (NpgsqlException)
        {
            return false;
        }
    }
}

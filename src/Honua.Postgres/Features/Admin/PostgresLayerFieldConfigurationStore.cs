// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Validation;
using Honua.Core.Features.Metadata.Domain.V2;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Admin;

/// <summary>
/// PostgreSQL store for operator-managed layer field configuration.
/// </summary>
internal sealed class PostgresLayerFieldConfigurationStore : ILayerFieldConfigurationStore
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string _fieldsTable;

    public PostgresLayerFieldConfigurationStore(IDatabaseConnectionProvider connectionProvider, string? schemaName = null)
    {
        _connectionProvider = connectionProvider.ThrowIfNull();
        _fieldsTable = Infrastructure.SchemaSearchPath.QualifyTable("layer_fields", schemaName);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LayerFieldConfiguration>> GetFieldConfigurationsAsync(
        int layerId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await GetFieldConfigurationsAsync(connection, layerId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LayerFieldConfiguration>> UpdateFieldConfigurationsAsync(
        int layerId,
        IReadOnlyList<LayerFieldConfigurationUpdate> updates,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        const string fieldNameParameter = "fieldName";
        const string aliasParameter = "alias";
        const string domainParameter = "domain";
        const string hiddenParameter = "hidden";

        string sql = $"""
            UPDATE {_fieldsTable}
            SET
                description = @{aliasParameter},
                domain = @{domainParameter},
                hidden = COALESCE(@{hiddenParameter}, hidden)
            WHERE layer_id = @layerId
              AND field_name = @{fieldNameParameter}
            """;

        foreach (var update in updates)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("layerId", layerId);
            command.Parameters.AddWithValue(fieldNameParameter, update.Name);
            command.Parameters.AddWithValue(aliasParameter, (object?)NormalizeAlias(update.Alias) ?? DBNull.Value);
            var domain = command.Parameters.Add(domainParameter, NpgsqlDbType.Jsonb);
            domain.Value = update.Domain is null
                ? DBNull.Value
                : JsonSerializer.Serialize(update.Domain, MetadataV2JsonContext.Default.MetadataV2FieldDomain);
            var hidden = command.Parameters.Add(hiddenParameter, NpgsqlDbType.Boolean);
            hidden.Value = update.Hidden.HasValue ? update.Hidden.Value : DBNull.Value;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var fields = await GetFieldConfigurationsAsync(connection, layerId, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return fields;
    }

    private async Task<IReadOnlyList<LayerFieldConfiguration>> GetFieldConfigurationsAsync(
        NpgsqlConnection connection,
        int layerId,
        CancellationToken cancellationToken)
    {
        return await GetFieldConfigurationsAsync(connection, layerId, transaction: null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<LayerFieldConfiguration>> GetFieldConfigurationsAsync(
        NpgsqlConnection connection,
        int layerId,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        string sql = $"""
            SELECT field_name, description, domain, hidden
            FROM {_fieldsTable}
            WHERE layer_id = @layerId
            ORDER BY field_order
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("layerId", layerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var fields = new List<LayerFieldConfiguration>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string name = reader.GetString(reader.GetOrdinal("field_name"));
            int aliasOrdinal = reader.GetOrdinal("description");
            string? alias = reader.IsDBNull(aliasOrdinal) ? null : reader.GetString(aliasOrdinal);
            int domainOrdinal = reader.GetOrdinal("domain");
            MetadataV2FieldDomain? domain = reader.IsDBNull(domainOrdinal)
                ? null
                : JsonSerializer.Deserialize(
                    reader.GetString(domainOrdinal),
                    MetadataV2JsonContext.Default.MetadataV2FieldDomain);
            bool hidden = reader.GetBoolean(reader.GetOrdinal("hidden"));

            fields.Add(new LayerFieldConfiguration(name, alias, domain, hidden));
        }

        return fields;
    }

    private static string? NormalizeAlias(string? alias)
    {
        var trimmed = alias?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

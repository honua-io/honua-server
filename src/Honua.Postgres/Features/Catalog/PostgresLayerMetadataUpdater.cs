// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Npgsql;

namespace Honua.Postgres.Features.Catalog;

/// <summary>
/// PostgreSQL implementation for updating layer metadata.
/// </summary>
internal sealed class PostgresLayerMetadataUpdater : ILayerMetadataUpdater
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string _layersTable;

    public PostgresLayerMetadataUpdater(IDatabaseConnectionProvider connectionProvider, string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));

        _layersTable = Infrastructure.SchemaSearchPath.QualifyTable("layers", schemaName);
    }

    /// <inheritdoc />
    public async Task UpdateLayerMetadataAsync(int layerId, CatalogMetadata metadata, CancellationToken cancellationToken = default)
    {
        var sql = $"""
            UPDATE {_layersTable}
            SET metadata = @metadata::jsonb
            WHERE layer_id = @layerId
            """;

        var metadataJson = JsonSerializer.Serialize(metadata, CatalogJsonContext.Default.CatalogMetadata);

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@layerId", layerId);
        _ = command.Parameters.AddWithValue("@metadata", metadataJson);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

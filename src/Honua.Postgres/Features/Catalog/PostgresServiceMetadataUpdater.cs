// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Npgsql;

namespace Honua.Postgres.Features.Catalog;

/// <summary>
/// PostgreSQL implementation for updating service metadata.
/// </summary>
internal sealed class PostgresServiceMetadataUpdater : IServiceMetadataUpdater
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string _servicesTable;

    public PostgresServiceMetadataUpdater(IDatabaseConnectionProvider connectionProvider, string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));

        _servicesTable = Infrastructure.SchemaSearchPath.QualifyTable("services", schemaName);
    }

    /// <inheritdoc />
    public async Task UpdateServiceMetadataAsync(string serviceName, CatalogMetadata metadata, CancellationToken cancellationToken = default)
    {
        var sql = $"""
            UPDATE {_servicesTable}
            SET metadata = @metadata::jsonb, updated_at = NOW()
            WHERE LOWER(service_name) = LOWER(@serviceName)
            """;

        var metadataJson = JsonSerializer.Serialize(metadata, CatalogJsonContext.Default.CatalogMetadata);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@serviceName", serviceName);
        _ = command.Parameters.AddWithValue("@metadata", metadataJson);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

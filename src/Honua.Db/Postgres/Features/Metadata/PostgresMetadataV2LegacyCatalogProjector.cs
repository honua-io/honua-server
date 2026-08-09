// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Postgres.Features.Metadata;

/// <summary>
/// PostgreSQL-backed <see cref="IMetadataV2LegacyCatalogProjector"/>. Reuses the same
/// <see cref="MetadataV2CompatSnapshotSql.BuildDocumentFromV1Catalog"/> synthesis the
/// read-time compat fallback uses (<see cref="PostgresMetadataV2GraphStore"/>), so the
/// legacy-catalog projection stays byte-identical to the snapshot a deployment serves
/// before its first real publish. (honua-server#2081.)
/// </summary>
internal sealed class PostgresMetadataV2LegacyCatalogProjector : IMetadataV2LegacyCatalogProjector
{
    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly string _environment;
    private readonly string _schemaName;

    public PostgresMetadataV2LegacyCatalogProjector(
        IAdoNetDatabaseConnectionProvider connectionProvider,
        string environment,
        string? schemaName = null)
    {
        ArgumentNullException.ThrowIfNull(connectionProvider);
        if (string.IsNullOrWhiteSpace(environment))
        {
            throw new ArgumentException("Environment must be set.", nameof(environment));
        }

        _connectionProvider = connectionProvider;
        _environment = environment;
        _schemaName = string.IsNullOrWhiteSpace(schemaName) ? "honua" : schemaName.Trim();
    }

    /// <inheritdoc />
    public async Task<MetadataV2Graph?> BuildFromLegacyCatalogAsync(CancellationToken cancellationToken = default)
    {
        var catalogSchema = SchemaSearchPath.ValidateAndQuote(_schemaName);
        var sql = MetadataV2CompatSnapshotSql.BuildDocumentFromV1Catalog
            .Replace(MetadataV2CompatSnapshotSql.CatalogSchemaPlaceholder, catalogSchema, StringComparison.Ordinal);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@environment", _environment);

        try
        {
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is not string json || string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var graph = JsonSerializer.Deserialize(json, MetadataV2JsonContext.Default.MetadataV2Graph);
            if (graph is null)
            {
                return null;
            }

            // No published service layers => an empty graph. Treat that as "nothing to
            // project" so callers do not activate an empty compat catalog over whatever
            // snapshot is already current.
            if (graph.Services.Count == 0 && graph.Resources.Count == 0)
            {
                return null;
            }

            return graph;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // Legacy V1 catalog tables are absent (e.g. a v2-only fixture database). There
            // is no catalog to project.
            return null;
        }
    }
}

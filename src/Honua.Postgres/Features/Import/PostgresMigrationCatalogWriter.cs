// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Postgres.Features.Import;

/// <summary>
/// PostgreSQL implementation of <see cref="IMigrationCatalogWriter"/>. Persists
/// migrated workspace and layer-group records into <c>honua.services</c> using
/// <c>INSERT ... ON CONFLICT DO NOTHING</c> so re-running an apply is idempotent.
/// </summary>
internal sealed partial class PostgresMigrationCatalogWriter : IMigrationCatalogWriter
{
    private static readonly string[] _defaultFormats = ["JSON", "GeoJSON"];
    private static readonly string[] _defaultCapabilities = ["Query", "Extract"];

    private readonly ILogger<PostgresMigrationCatalogWriter> _logger;

    public PostgresMigrationCatalogWriter(ILogger<PostgresMigrationCatalogWriter> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<MigrationCatalogWriteOutcome> EnsureCatalogServiceAsync(
        string connectionString,
        MigrationCatalogServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ServiceName);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Idempotent upsert: ON CONFLICT (service_name) DO NOTHING lets the apply
        // plan re-run safely against the same target without duplicating catalog rows.
        const string sql = """
            INSERT INTO honua.services (
                service_name,
                description,
                srid,
                supported_formats,
                capabilities
            )
            VALUES (@serviceName, @description, @srid, @formats, @capabilities)
            ON CONFLICT (service_name) DO NOTHING
            RETURNING service_name;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@serviceName", request.ServiceName);
        command.Parameters.AddWithValue("@description", request.Description);
        command.Parameters.AddWithValue("@srid", request.Srid);
        command.Parameters.AddWithValue("@formats", _defaultFormats);
        command.Parameters.AddWithValue("@capabilities", _defaultCapabilities);

        var inserted = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var outcome = inserted is null
            ? MigrationCatalogWriteOutcome.AlreadyExists
            : MigrationCatalogWriteOutcome.Created;

        Log.CatalogServicePersisted(_logger, request.EntryKind, request.ServiceName, outcome.ToString());
        return outcome;
    }

    private static partial class Log
    {
        [LoggerMessage(7960, LogLevel.Information, "Migration catalog writer ensured {EntryKind} service '{ServiceName}' ({Outcome})")]
        public static partial void CatalogServicePersisted(ILogger logger, string entryKind, string serviceName, string outcome);
    }
}

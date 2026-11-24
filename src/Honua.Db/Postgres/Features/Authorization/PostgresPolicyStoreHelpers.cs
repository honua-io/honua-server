// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Authorization;

internal static class PostgresPolicyStoreHelpers
{
    public static async Task<IReadOnlyList<TPolicy>> GetEffectivePoliciesAsync<TPolicy>(
        IAdoNetDatabaseConnectionProvider connectionProvider,
        string table,
        string selectColumns,
        IReadOnlyList<string> roles,
        string service,
        string layer,
        Func<NpgsqlCommand, CancellationToken, Task<IReadOnlyList<TPolicy>>> readPoliciesAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(layer);

        // Resolve every policy whose scope matches the (service, layer) — including
        // "*" wildcards — and whose role is "*" or one of the principal's roles
        // (case-insensitive). A single set-based query keeps this cheap and correct
        // under concurrent writes; role filtering uses a lowered-name ANY match.
        var sql = $"""
            SELECT {selectColumns}
            FROM {table}
            WHERE (service = '*' OR LOWER(service) = LOWER(@service))
              AND (layer = '*' OR LOWER(layer) = LOWER(@layer))
              AND (role = '*' OR LOWER(role) = ANY(@role_names))
            ORDER BY created_at
            """;

        var loweredNames = roles
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray();

        await using var connection = await connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("service", NpgsqlDbType.Varchar, service);
        command.Parameters.AddWithValue("layer", NpgsqlDbType.Varchar, layer);
        command.Parameters.AddWithValue("role_names", NpgsqlDbType.Array | NpgsqlDbType.Text, loweredNames);

        try
        {
            return await readPoliciesAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return [];
        }
    }
}

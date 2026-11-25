// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Authorization;

internal sealed class EffectivePolicyQuery<TPolicy>
{
    public required IAdoNetDatabaseConnectionProvider ConnectionProvider { get; init; }
    public required string Table { get; init; }
    public required string SelectColumns { get; init; }
    public required IReadOnlyList<string> Roles { get; init; }
    public required string Service { get; init; }
    public required string Layer { get; init; }
    public required Func<NpgsqlCommand, CancellationToken, Task<IReadOnlyList<TPolicy>>> ReadPoliciesAsync { get; init; }
}

internal static class PostgresPolicyStoreHelpers
{
    public static async Task<IReadOnlyList<TPolicy>> GetEffectivePoliciesAsync<TPolicy>(
        EffectivePolicyQuery<TPolicy> query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.Roles);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.Service);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.Layer);

        await using var connection = await query.ConnectionProvider
            .OpenNpgsqlConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = CreateEffectivePolicyCommand(connection, query);

        try
        {
            return await query.ReadPoliciesAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return [];
        }
    }

    private static NpgsqlCommand CreateEffectivePolicyCommand<TPolicy>(
        NpgsqlConnection connection,
        EffectivePolicyQuery<TPolicy> query)
    {
        // Resolve every policy whose scope matches (service, layer) including "*" wildcards
        // and whose role is "*" or one of the principal's roles (case-insensitive).
        var sql = $"""
            SELECT {query.SelectColumns}
            FROM {query.Table}
            WHERE (service = '*' OR LOWER(service) = LOWER(@service))
              AND (layer = '*' OR LOWER(layer) = LOWER(@layer))
              AND (role = '*' OR LOWER(role) = ANY(@role_names))
            ORDER BY created_at
            """;

        var loweredNames = query.Roles
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray();

        var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("service", NpgsqlDbType.Varchar, query.Service);
        command.Parameters.AddWithValue("layer", NpgsqlDbType.Varchar, query.Layer);
        command.Parameters.AddWithValue("role_names", NpgsqlDbType.Array | NpgsqlDbType.Text, loweredNames);
        return command;
    }
}

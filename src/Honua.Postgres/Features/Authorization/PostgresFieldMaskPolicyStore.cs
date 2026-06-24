// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Authorization;

/// <summary>
/// PostgreSQL-backed <see cref="IFieldMaskPolicyStore"/> (#1940). Persists field-level
/// security (column masking) policies to the <c>honua.rbac_field_mask_policies</c> table
/// created by migration 068 so per-layer attribute-masking rules survive process restart
/// and are shared across scaled nodes. Mirrors <c>PostgresRlsPolicyStore</c> (#502).
/// </summary>
internal sealed class PostgresFieldMaskPolicyStore : IFieldMaskPolicyStore
{
    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly string _table;

    public PostgresFieldMaskPolicyStore(IAdoNetDatabaseConnectionProvider connectionProvider, string? schemaName = null)
    {
        ArgumentNullException.ThrowIfNull(connectionProvider);
        _connectionProvider = connectionProvider;
        _table = SchemaSearchPath.QualifyTable("rbac_field_mask_policies", schemaName);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FieldMaskPolicy>> ListPoliciesAsync(CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT policy_id, role, service, layer, attribute, description, created_at, updated_at
            FROM {_table}
            ORDER BY created_at DESC
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        return await ReadPoliciesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<FieldMaskPolicy?> GetPolicyAsync(Guid policyId, CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT policy_id, role, service, layer, attribute, description, created_at, updated_at
            FROM {_table}
            WHERE policy_id = @policy_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("policy_id", NpgsqlDbType.Uuid, policyId);
        var policies = await ReadPoliciesAsync(command, cancellationToken).ConfigureAwait(false);
        return policies.Count == 0 ? null : policies[0];
    }

    /// <inheritdoc />
    public async Task<FieldMaskPolicy> CreatePolicyAsync(FieldMaskPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var sql = $"""
            INSERT INTO {_table}
                (policy_id, role, service, layer, attribute, description, created_at, updated_at)
            VALUES
                (@policy_id, @role, @service, @layer, @attribute, @description, @created_at, @updated_at)
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("policy_id", NpgsqlDbType.Uuid, policy.PolicyId);
        command.Parameters.AddWithValue("role", NpgsqlDbType.Varchar, policy.Role);
        command.Parameters.AddWithValue("service", NpgsqlDbType.Varchar, policy.Service);
        command.Parameters.AddWithValue("layer", NpgsqlDbType.Varchar, policy.Layer);
        command.Parameters.AddWithValue("attribute", NpgsqlDbType.Varchar, policy.Attribute);
        command.Parameters.AddWithValue("description", NpgsqlDbType.Text, (object?)policy.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, policy.CreatedAt);
        command.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, policy.UpdatedAt);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException(
                $"A field-mask policy for role '{policy.Role}', service '{policy.Service}', layer '{policy.Layer}', attribute '{policy.Attribute}' already exists.",
                ex);
        }

        return policy;
    }

    /// <inheritdoc />
    public async Task<bool> DeletePolicyAsync(Guid policyId, CancellationToken cancellationToken = default)
    {
        var sql = $"DELETE FROM {_table} WHERE policy_id = @policy_id";

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("policy_id", NpgsqlDbType.Uuid, policyId);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FieldMaskPolicy>> GetEffectivePoliciesAsync(
        IReadOnlyList<string> roles,
        string service,
        string layer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(layer);

        // Resolve every policy whose scope matches the (service, layer) — including
        // "*" wildcards — and whose role is "*" or one of the principal's roles
        // (case-insensitive). A single set-based query keeps this cheap and correct
        // under concurrent writes; role filtering uses a lowered-name ANY match.
        var sql = $"""
            SELECT policy_id, role, service, layer, attribute, description, created_at, updated_at
            FROM {_table}
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

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("service", NpgsqlDbType.Varchar, service);
        command.Parameters.AddWithValue("layer", NpgsqlDbType.Varchar, layer);
        command.Parameters.AddWithValue("role_names", NpgsqlDbType.Array | NpgsqlDbType.Text, loweredNames);

        try
        {
            return await ReadPoliciesAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // Fresh/legacy DB where migration 068 has not created the table: treat
            // as "no field-mask policies" so query paths behave exactly as before
            // (no masking), matching the rbac_* graceful-degradation pattern.
            return [];
        }
    }

    private static async Task<IReadOnlyList<FieldMaskPolicy>> ReadPoliciesAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var policies = new List<FieldMaskPolicy>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            policies.Add(new FieldMaskPolicy
            {
                PolicyId = reader.GetGuid(0),
                Role = reader.GetString(1),
                Service = reader.GetString(2),
                Layer = reader.GetString(3),
                Attribute = reader.GetString(4),
                Description = reader.IsDBNull(5) ? null : reader.GetString(5),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(6),
                UpdatedAt = reader.GetFieldValue<DateTimeOffset>(7),
            });
        }

        return policies;
    }
}

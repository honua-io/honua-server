// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Authorization;

internal sealed partial class PostgresRlsPolicyStore
{
    public bool CanResolveWith(IFieldMaskPolicyStore fieldStore)
        => fieldStore is PostgresFieldMaskPolicyStore masks && masks.HasSameCatalog(_connectionProvider, _table);

    public async Task<EffectiveReadPolicies> GetEffectiveReadPoliciesAsync(
        IFieldMaskPolicyStore fieldStore, IReadOnlyList<string> roles,
        IReadOnlyCollection<string> services, string layer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(layer);
        if (!CanResolveWith(fieldStore))
        {
            throw new ArgumentException("The registered policy stores must share their provider and catalog schema.", nameof(fieldStore));
        }
        var scopes = new HashSet<string>(services, StringComparer.OrdinalIgnoreCase);
        if (scopes.Count == 0)
        {
            scopes.Add("*");
        }
        var names = roles.Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim().ToLowerInvariant()).Distinct().ToArray();
        try
        {
            return await ReadCombinedAsync((PostgresFieldMaskPolicyStore)fieldStore, names,
                scopes.ToArray(), layer, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // The failed implicit batch transaction and its lease are disposed before
            // independent legacy reads. A missing table must not drop the other set.
            var rows = new Dictionary<Guid, RlsPolicy>();
            var fields = new Dictionary<Guid, FieldMaskPolicy>();
            foreach (var service in scopes)
            {
                foreach (var policy in await GetEffectivePoliciesAsync(roles, service, layer, cancellationToken).ConfigureAwait(false))
                {
                    rows[policy.PolicyId] = policy;
                }
                foreach (var policy in await fieldStore.GetEffectivePoliciesAsync(roles, service, layer, cancellationToken).ConfigureAwait(false))
                {
                    fields[policy.PolicyId] = policy;
                }
            }
            return new(rows.Values.ToArray(), fields.Values.ToArray());
        }
    }

    private async Task<EffectiveReadPolicies> ReadCombinedAsync(
        PostgresFieldMaskPolicyStore masks, string[] roles, string[] services,
        string layer, CancellationToken cancellationToken)
    {
        // Keep service case folding in PostgreSQL, matching each standalone lookup.
        const string predicate = """
            WHERE (service = '*' OR LOWER(service) IN (SELECT LOWER(scope) FROM unnest(@services) AS scopes(scope)))
              AND (layer = '*' OR LOWER(layer) = LOWER(@layer))
              AND (role = '*' OR LOWER(role) = ANY(@role_names))
            ORDER BY created_at
            """;
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var batch = new NpgsqlBatch(connection) { EnableErrorBarriers = false };
        batch.BatchCommands.Add(CreatePolicyCommand($"""
            SELECT policy_id, role, service, layer, attribute, claim_type, comparison, description, created_at, updated_at
            FROM {_table}
            {predicate}
            """, roles, services, layer));
        batch.BatchCommands.Add(CreatePolicyCommand($"""
            SELECT policy_id, role, service, layer, attribute, description, created_at, updated_at
            FROM {masks.Table}
            {predicate}
            """, roles, services, layer));
        await using var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<RlsPolicy>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new RlsPolicy
            {
                PolicyId = reader.GetGuid(0), Role = reader.GetString(1), Service = reader.GetString(2),
                Layer = reader.GetString(3), Attribute = reader.GetString(4), ClaimType = reader.GetString(5),
                Comparison = (RlsComparison)reader.GetInt16(6), Description = reader.IsDBNull(7) ? null : reader.GetString(7),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(8), UpdatedAt = reader.GetFieldValue<DateTimeOffset>(9)
            });
        }
        if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The field-policy result is missing from the read-policy batch.");
        }
        var fields = new List<FieldMaskPolicy>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            fields.Add(new FieldMaskPolicy
            {
                PolicyId = reader.GetGuid(0), Role = reader.GetString(1), Service = reader.GetString(2),
                Layer = reader.GetString(3), Attribute = reader.GetString(4), Description = reader.IsDBNull(5) ? null : reader.GetString(5),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(6), UpdatedAt = reader.GetFieldValue<DateTimeOffset>(7)
            });
        }
        return new(rows, fields);
    }

    private static NpgsqlBatchCommand CreatePolicyCommand(string sql, string[] roles, string[] services, string layer)
    {
        var command = new NpgsqlBatchCommand(sql);
        command.Parameters.AddWithValue("services", NpgsqlDbType.Array | NpgsqlDbType.Text, services);
        command.Parameters.AddWithValue("layer", NpgsqlDbType.Varchar, layer);
        command.Parameters.AddWithValue("role_names", NpgsqlDbType.Array | NpgsqlDbType.Text, roles);
        return command;
    }
}

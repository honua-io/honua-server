// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Npgsql;

namespace Honua.Server.Features.AgentWorkspace;

internal sealed record AgentWorkspaceIdentity(string WorkspaceId, string OwnerActorId, string TargetId, string? TenantId);
internal sealed record LocalProcessError(string Error);

internal sealed class AgentWorkspaceIdentityStore(JobStoreDatabase database)
{
    public Task CreateAsync(AgentWorkspaceIdentity identity, CancellationToken cancellationToken) => database.ExecuteAsync(
        async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                INSERT INTO {database.Table("agent_workspace_identities")} (workspace_id, owner_actor_id, target_id, tenant_id)
                VALUES ($1, $2, $3, $4)
                ON CONFLICT (workspace_id) DO NOTHING
                """;
            command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = identity.WorkspaceId });
            command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = identity.OwnerActorId });
            command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = identity.TargetId });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = (object?)identity.TenantId ?? DBNull.Value });
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, StatementRepeat.SafeToRepeat, cancellationToken);

    public Task<AgentWorkspaceIdentity?> GetAsync(string workspaceId, CancellationToken cancellationToken) => database.ExecuteAsync<AgentWorkspaceIdentity?>(
        async (connection, _, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT owner_actor_id, target_id, tenant_id FROM {database.Table("agent_workspace_identities")} WHERE workspace_id = $1";
            command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = workspaceId });
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            return await reader.ReadAsync(token).ConfigureAwait(false)
                ? new AgentWorkspaceIdentity(workspaceId, reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2))
                : null;
        }, StatementRepeat.SafeToRepeat, cancellationToken);
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using Honua.Infrastructure.Authentication;
using Npgsql;

namespace Honua.Server.Features.Portal.Sharing;

/// <summary>Shared, restart-safe Portal community groups. Membership updates lock the group row.</summary>
internal sealed class PostgresPortalGroupStore(
    NpgsqlDataSource dataSource,
    TimeProvider timeProvider,
    string? schema = null) : IPortalGroupStore
{
    private readonly string _table = PortalSharingTables.Qualify("portal_community_groups", schema);

    public async Task<IReadOnlyList<PortalGroupRecord>> ListAsync(CancellationToken cancellationToken)
    {
        var result = new List<PortalGroupRecord>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"SELECT {Columns} FROM {_table} ORDER BY created_at, group_id", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(Read(reader));
        }

        return result;
    }

    public async Task<PortalGroupRecord?> GetAsync(string groupId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return null;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadOneAsync(connection, null, groupId, forUpdate: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PortalGroupRecord> CreateAsync(
        PortalGroupRegistration registration, string owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        var now = timeProvider.GetUtcNow();
        var record = new PortalGroupRecord(
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            registration.Title.Trim(), registration.Description?.Trim(), owner,
            NormalizeAccess(registration.Access), Normalize(registration.Tags), [owner],
            now, now, registration.TenantId);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {_table} (group_id, tenant_key, title, description, owner_name,
                access_level, tags, members, created_at, modified_at)
            VALUES (@id, @tenant, @title, @description, @owner, @access, @tags, @members, @created, @modified)
            """, connection);
        AddRecord(command, record);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<PortalGroupRecord?> DeleteAsync(string groupId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return null;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"DELETE FROM {_table} WHERE group_id = @id RETURNING {Columns}", connection);
        command.Parameters.AddWithValue("id", groupId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public Task<PortalGroupRecord?> AddUsersAsync(
        string groupId, IReadOnlyList<string> usernames, CancellationToken cancellationToken)
        => MutateMembersAsync(groupId, usernames, add: true, cancellationToken);

    public Task<PortalGroupRecord?> RemoveUsersAsync(
        string groupId, IReadOnlyList<string> usernames, CancellationToken cancellationToken)
        => MutateMembersAsync(groupId, usernames, add: false, cancellationToken);

    // The authorization hot path is synchronous. On Postgres it reads the fresh
    // request-scoped audience snapshot, never a blocking database call or TTL cache.
    public IReadOnlyList<string> GetGroupIdsForUser(string username, string? tenantId = null)
        => throw new InvalidOperationException("Durable Portal membership requires the request audience snapshot.");

    private async Task<PortalGroupRecord?> MutateMembersAsync(
        string groupId, IReadOnlyList<string> usernames, bool add, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return null;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var current = await ReadOneAsync(connection, transaction, groupId, forUpdate: true, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return null;
        }

        var requested = Normalize(usernames);
        var members = current.Members.ToList();
        if (add)
        {
            foreach (var user in requested.Where(user => !members.Contains(user, StringComparer.OrdinalIgnoreCase)))
            {
                members.Add(user);
            }
        }
        else
        {
            members.RemoveAll(user => !string.Equals(user, current.Owner, StringComparison.OrdinalIgnoreCase)
                && requested.Contains(user, StringComparer.OrdinalIgnoreCase));
        }

        var updated = current with { Members = members, Modified = timeProvider.GetUtcNow() };
        await using var command = new NpgsqlCommand($"""
            UPDATE {_table} SET members = @members, modified_at = @modified WHERE group_id = @id
            """, connection, transaction);
        command.Parameters.AddWithValue("members", members.ToArray());
        command.Parameters.AddWithValue("modified", updated.Modified);
        command.Parameters.AddWithValue("id", groupId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private async Task<PortalGroupRecord?> ReadOneAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string id,
        bool forUpdate, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT {Columns} FROM {_table} WHERE group_id = @id" + (forUpdate ? " FOR UPDATE" : string.Empty),
            connection, transaction);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    private const string Columns = "group_id, tenant_key, title, description, owner_name, access_level, tags, members, created_at, modified_at";

    private static PortalGroupRecord Read(NpgsqlDataReader reader)
        => new(reader.GetString(0), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetFieldValue<string[]>(6),
            reader.GetFieldValue<string[]>(7), reader.GetFieldValue<DateTimeOffset>(8),
            reader.GetFieldValue<DateTimeOffset>(9),
            reader.GetString(1) is { Length: > 0 } tenant ? tenant : null);

    private static void AddRecord(NpgsqlCommand command, PortalGroupRecord record)
    {
        command.Parameters.AddWithValue("id", record.Id);
        command.Parameters.AddWithValue("tenant", record.TenantId ?? string.Empty);
        command.Parameters.AddWithValue("title", record.Title);
        command.Parameters.AddWithValue("description", (object?)record.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("owner", record.Owner);
        command.Parameters.AddWithValue("access", record.Access);
        command.Parameters.AddWithValue("tags", record.Tags.ToArray());
        command.Parameters.AddWithValue("members", record.Members.ToArray());
        command.Parameters.AddWithValue("created", record.Created);
        command.Parameters.AddWithValue("modified", record.Modified);
    }

    private static string NormalizeAccess(string? access)
        => string.Equals(access, PortalGroupAccess.Public, StringComparison.OrdinalIgnoreCase)
            ? PortalGroupAccess.Public
            : string.Equals(access, PortalGroupAccess.Org, StringComparison.OrdinalIgnoreCase)
                ? PortalGroupAccess.Org : PortalGroupAccess.Private;

    private static string[] Normalize(IReadOnlyList<string>? values)
        => values?.Select(value => value?.Trim() ?? string.Empty)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
}

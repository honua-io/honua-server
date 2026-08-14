// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Identity.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Identity;

/// <summary>
/// PostgreSQL-backed SCIM 2.0 group store (#510, durable per #3141). Each group maps to a
/// Honua role named after the group's display name; adding or removing a member
/// synchronizes that role onto the member's <c>honua.managed_user_roles</c> rows in the
/// same transaction, so SCIM group changes immediately drive RBAC role assignment and are
/// visible on every replica. Replaces the process-local <c>InMemoryScimGroupStore</c> as
/// the registered implementation on Postgres profiles.
/// </summary>
/// <remarks>
/// Members may reference users that have not been provisioned yet; the membership row is
/// kept and the role sync is a no-op until the user exists (in-memory store parity, which
/// is why <c>scim_group_members.user_id</c> carries no foreign key to
/// <c>managed_users</c>).
/// </remarks>
internal sealed class PostgresScimGroupStore : IScimGroupStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _groupsTable;
    private readonly string _membersTable;
    private readonly string _usersTable;
    private readonly string _rolesTable;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresScimGroupStore"/> class.
    /// </summary>
    /// <param name="dataSource">The Npgsql data source.</param>
    /// <param name="schemaName">Optional schema override (used by tests for isolated
    /// schemas); defaults to the application schema.</param>
    public PostgresScimGroupStore(NpgsqlDataSource dataSource, string? schemaName = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
        _groupsTable = SchemaSearchPath.QualifyTable("scim_groups", schemaName);
        _membersTable = SchemaSearchPath.QualifyTable("scim_group_members", schemaName);
        _usersTable = SchemaSearchPath.QualifyTable("managed_users", schemaName);
        _rolesTable = SchemaSearchPath.QualifyTable("managed_user_roles", schemaName);
    }

    /// <inheritdoc />
    public async Task<ScimGroup?> CreateGroupAsync(ScimGroupProvisioning provisioning, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provisioning);

        var now = DateTimeOffset.UtcNow;
        var displayName = provisioning.DisplayName.Trim();
        var members = NormalizeMembers(provisioning.MemberUserIds);
        var groupId = Guid.NewGuid().ToString("D");

        var insert = $"""
            INSERT INTO {_groupsTable} (group_id, display_name, created_at, updated_at)
            VALUES (@group_id, @display_name, @created_at, @updated_at)
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await PostgresUserIdentityLock.AcquireManyAsync(connection, transaction, members, cancellationToken).ConfigureAwait(false);

        try
        {
            await using (var command = new NpgsqlCommand(insert, connection, transaction))
            {
                command.Parameters.AddWithValue("group_id", NpgsqlDbType.Varchar, groupId);
                command.Parameters.AddWithValue("display_name", NpgsqlDbType.Varchar, displayName);
                command.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, now);
                command.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, now);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var userId in members)
            {
                await AddMemberAsync(connection, transaction, groupId, userId, cancellationToken).ConfigureAwait(false);
                await GrantRoleAsync(connection, transaction, userId, displayName, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // A group with that display name already exists: SCIM 409 uniqueness.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        return new ScimGroup
        {
            GroupId = groupId,
            DisplayName = displayName,
            MemberUserIds = members,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <inheritdoc />
    public async Task<ScimGroup?> GetGroupAsync(string groupId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return null;
        }

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            return await ReadGroupAsync(connection, transaction: null, groupId, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // Fresh/legacy DB where migration 106 has not created the SCIM group tables.
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<ScimGroupPage> ListGroupsAsync(ScimGroupQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var where = string.IsNullOrEmpty(query.DisplayNameEquals)
            ? string.Empty
            : "WHERE LOWER(g.display_name) = LOWER(@display_name)";

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            int totalCount;
            var countSql = $"SELECT COUNT(*) FROM {_groupsTable} g {where}";
            await using (var countCommand = new NpgsqlCommand(countSql, connection))
            {
                if (!string.IsNullOrEmpty(query.DisplayNameEquals))
                {
                    countCommand.Parameters.AddWithValue("display_name", NpgsqlDbType.Varchar, query.DisplayNameEquals);
                }

                totalCount = Convert.ToInt32(
                    await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            var pageSql = $"""
                {SelectGroupsSql(where)}
                ORDER BY LOWER(g.display_name)
                LIMIT @limit OFFSET @offset
                """;

            var groups = new List<ScimGroup>();
            await using (var pageCommand = new NpgsqlCommand(pageSql, connection))
            {
                if (!string.IsNullOrEmpty(query.DisplayNameEquals))
                {
                    pageCommand.Parameters.AddWithValue("display_name", NpgsqlDbType.Varchar, query.DisplayNameEquals);
                }

                pageCommand.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, Math.Max(0, query.Count));
                pageCommand.Parameters.AddWithValue("offset", NpgsqlDbType.Integer, Math.Max(0, query.StartIndex - 1));

                await using var reader = await pageCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    groups.Add(ReadGroup(reader));
                }
            }

            return new ScimGroupPage { Groups = groups, TotalCount = totalCount };
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return new ScimGroupPage { Groups = [], TotalCount = 0 };
        }
    }

    /// <inheritdoc />
    public async Task<ScimGroup?> ReplaceGroupAsync(string groupId, ScimGroupProvisioning provisioning, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provisioning);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await PostgresUserIdentityLock.AcquireGroupAsync(
            connection,
            transaction,
            groupId,
            cancellationToken).ConfigureAwait(false);

        var existing = await ReadGroupAsync(connection, transaction, groupId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var newMembers = NormalizeMembers(provisioning.MemberUserIds);
        var newName = provisioning.DisplayName.Trim();

        await PostgresUserIdentityLock.AcquireManyAsync(
            connection,
            transaction,
            existing.MemberUserIds.Concat(newMembers),
            cancellationToken).ConfigureAwait(false);

        // A rename re-maps the role: revoke the old role from everyone, then grant the new.
        var renamed = !existing.DisplayName.Equals(newName, StringComparison.OrdinalIgnoreCase);

        try
        {
            // Revoke the (old) role from members that are leaving or affected by a rename.
            foreach (var userId in existing.MemberUserIds.Where(userId =>
                renamed || !newMembers.Contains(userId, StringComparer.OrdinalIgnoreCase)))
            {
                await RevokeRoleAsync(connection, transaction, userId, existing.DisplayName, cancellationToken).ConfigureAwait(false);
            }

            // Replace the member set, then grant the (new) role to the resulting members.
            var deleteMembers = $"DELETE FROM {_membersTable} WHERE group_id = @group_id";
            await using (var deleteCommand = new NpgsqlCommand(deleteMembers, connection, transaction))
            {
                deleteCommand.Parameters.AddWithValue("group_id", NpgsqlDbType.Varchar, existing.GroupId);
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var userId in newMembers)
            {
                await AddMemberAsync(connection, transaction, existing.GroupId, userId, cancellationToken).ConfigureAwait(false);
                await GrantRoleAsync(connection, transaction, userId, newName, cancellationToken).ConfigureAwait(false);
            }

            var updateGroup = $"""
                UPDATE {_groupsTable}
                SET display_name = @display_name, updated_at = NOW()
                WHERE group_id = @group_id
                """;
            await using (var updateCommand = new NpgsqlCommand(updateGroup, connection, transaction))
            {
                updateCommand.Parameters.AddWithValue("group_id", NpgsqlDbType.Varchar, existing.GroupId);
                updateCommand.Parameters.AddWithValue("display_name", NpgsqlDbType.Varchar, newName);
                await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var updated = await ReadGroupAsync(connection, transaction, existing.GroupId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
            return updated;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Renaming into an existing group display name hits the unique index; translate
            // rather than leak a raw PostgresException to the SCIM surface.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"A group named '{newName}' already exists.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<ScimGroup?> UpdateMembersAsync(string groupId, ScimGroupMemberChange change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await PostgresUserIdentityLock.AcquireGroupAsync(
            connection,
            transaction,
            groupId,
            cancellationToken).ConfigureAwait(false);

        var existing = await ReadGroupAsync(connection, transaction, groupId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        await PostgresUserIdentityLock.AcquireManyAsync(
            connection,
            transaction,
            change.Remove.Concat(change.Add),
            cancellationToken).ConfigureAwait(false);

        foreach (var userId in NormalizeMembers(change.Remove))
        {
            var removed = await RemoveMemberAsync(connection, transaction, existing.GroupId, userId, cancellationToken).ConfigureAwait(false);
            if (removed)
            {
                await RevokeRoleAsync(connection, transaction, userId, existing.DisplayName, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var userId in NormalizeMembers(change.Add))
        {
            var added = await AddMemberAsync(connection, transaction, existing.GroupId, userId, cancellationToken).ConfigureAwait(false);
            if (added)
            {
                await GrantRoleAsync(connection, transaction, userId, existing.DisplayName, cancellationToken).ConfigureAwait(false);
            }
        }

        var touch = $"UPDATE {_groupsTable} SET updated_at = NOW() WHERE group_id = @group_id";
        await using (var touchCommand = new NpgsqlCommand(touch, connection, transaction))
        {
            touchCommand.Parameters.AddWithValue("group_id", NpgsqlDbType.Varchar, existing.GroupId);
            await touchCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var updated = await ReadGroupAsync(connection, transaction, existing.GroupId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteGroupAsync(string groupId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await PostgresUserIdentityLock.AcquireGroupAsync(
            connection,
            transaction,
            groupId,
            cancellationToken).ConfigureAwait(false);

        var existing = await ReadGroupAsync(connection, transaction, groupId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await PostgresUserIdentityLock.AcquireManyAsync(
            connection,
            transaction,
            existing.MemberUserIds,
            cancellationToken).ConfigureAwait(false);

        foreach (var userId in existing.MemberUserIds)
        {
            await RevokeRoleAsync(connection, transaction, userId, existing.DisplayName, cancellationToken).ConfigureAwait(false);
        }

        var delete = $"DELETE FROM {_groupsTable} WHERE group_id = @group_id";
        await using (var deleteCommand = new NpgsqlCommand(delete, connection, transaction))
        {
            deleteCommand.Parameters.AddWithValue("group_id", NpgsqlDbType.Varchar, existing.GroupId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    // ---- helpers -----------------------------------------------------------------------

    private string SelectGroupsSql(string where) => $"""
        SELECT g.group_id, g.display_name, g.created_at, g.updated_at,
               COALESCE(ARRAY_AGG(m.user_id ORDER BY LOWER(m.user_id)) FILTER (WHERE m.user_id IS NOT NULL), ARRAY[]::VARCHAR[]) AS members
        FROM {_groupsTable} g
        LEFT JOIN {_membersTable} m ON m.group_id = g.group_id
        {where}
        GROUP BY g.group_id
        """;

    private static ScimGroup ReadGroup(NpgsqlDataReader reader) => new()
    {
        GroupId = reader.GetString(0),
        DisplayName = reader.GetString(1),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(2),
        UpdatedAt = reader.GetFieldValue<DateTimeOffset>(3),
        MemberUserIds = reader.GetFieldValue<string[]>(4),
    };

    private async Task<ScimGroup?> ReadGroupAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string groupId,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            {SelectGroupsSql("WHERE g.group_id = @group_id")}
            LIMIT 1
            """;

        try
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("group_id", NpgsqlDbType.Varchar, groupId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadGroup(reader) : null;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return null;
        }
    }

    /// <summary>
    /// Adds a member row. Returns whether a new row was inserted (an existing membership is
    /// idempotent and must not re-grant the role).
    /// </summary>
    private async Task<bool> AddMemberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string groupId,
        string userId,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO {_membersTable} (group_id, user_id)
            VALUES (@group_id, @user_id)
            ON CONFLICT (group_id, LOWER(user_id)) DO NOTHING
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("group_id", NpgsqlDbType.Varchar, groupId);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <summary>
    /// Removes a member row (case-insensitive). Returns whether a row was removed.
    /// </summary>
    private async Task<bool> RemoveMemberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string groupId,
        string userId,
        CancellationToken cancellationToken)
    {
        var sql = $"DELETE FROM {_membersTable} WHERE group_id = @group_id AND LOWER(user_id) = LOWER(@user_id)";

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("group_id", NpgsqlDbType.Varchar, groupId);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <summary>
    /// Grants the group's mapped role to a user. No-op when the user has not been
    /// provisioned (in-memory store parity).
    /// </summary>
    private async Task GrantRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string userId,
        string role,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            WITH granted AS (
                INSERT INTO {_rolesTable} (user_id, role)
                SELECT u.user_id, @role FROM {_usersTable} u WHERE LOWER(u.user_id) = LOWER(@user_id)
                ON CONFLICT (user_id, LOWER(role)) DO NOTHING
                RETURNING user_id
            )
            UPDATE {_usersTable} SET updated_at = NOW() WHERE user_id IN (SELECT user_id FROM granted)
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId);
        command.Parameters.AddWithValue("role", NpgsqlDbType.Varchar, role);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Revokes the group's mapped role from a user. No-op when the user or role assignment
    /// is absent (in-memory store parity).
    /// </summary>
    private async Task RevokeRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string userId,
        string role,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            WITH revoked AS (
                DELETE FROM {_rolesTable}
                WHERE LOWER(user_id) = LOWER(@user_id) AND LOWER(role) = LOWER(@role)
                RETURNING user_id
            )
            UPDATE {_usersTable} SET updated_at = NOW() WHERE user_id IN (SELECT user_id FROM revoked)
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId);
        command.Parameters.AddWithValue("role", NpgsqlDbType.Varchar, role);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static List<string> NormalizeMembers(IReadOnlyList<string> members)
        => members
            .Where(static m => !string.IsNullOrWhiteSpace(m))
            .Select(static m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

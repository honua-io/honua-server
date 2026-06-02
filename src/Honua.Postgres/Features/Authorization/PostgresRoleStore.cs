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
/// PostgreSQL-backed <see cref="IRoleStore"/> (#1374). Persists role
/// definitions, their per-operation permission grants, and role memberships to
/// the <c>honua.rbac_*</c> tables created by migration 041, so RBAC survives
/// process restart and is shared across scaled nodes. Replaces the process-local
/// <c>InMemoryRoleStore</c> as the registered implementation.
/// </summary>
internal sealed class PostgresRoleStore : IRoleStore
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string _rolesTable;
    private readonly string _permissionsTable;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresRoleStore"/> class.
    /// </summary>
    /// <param name="connectionProvider">The database connection provider.</param>
    /// <param name="schemaName">Optional schema override (used by tests for
    /// isolated schemas); defaults to the application schema.</param>
    public PostgresRoleStore(IDatabaseConnectionProvider connectionProvider, string? schemaName = null)
    {
        ArgumentNullException.ThrowIfNull(connectionProvider);
        _connectionProvider = connectionProvider;
        _rolesTable = SchemaSearchPath.QualifyTable("rbac_roles", schemaName);
        _permissionsTable = SchemaSearchPath.QualifyTable("rbac_role_permissions", schemaName);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RoleDefinition>> ListRolesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        var roles = await ReadRolesAsync(connection, roleId: null, cancellationToken).ConfigureAwait(false);
        return roles;
    }

    /// <inheritdoc />
    public async Task<RoleDefinition?> GetRoleAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        var roles = await ReadRolesAsync(connection, roleId, cancellationToken).ConfigureAwait(false);
        return roles.Count == 0 ? null : roles[0];
    }

    /// <inheritdoc />
    public async Task<RoleDefinition> CreateRoleAsync(RoleDefinition role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(role);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var insertRole = $"""
            INSERT INTO {_rolesTable} (role_id, name, description, is_built_in, created_at, updated_at)
            VALUES (@role_id, @name, @description, @is_built_in, @created_at, @updated_at)
            """;

        try
        {
            await using (var command = new NpgsqlCommand(insertRole, connection.Connection, transaction))
            {
                command.Parameters.AddWithValue("role_id", NpgsqlDbType.Uuid, role.RoleId);
                command.Parameters.AddWithValue("name", NpgsqlDbType.Varchar, role.Name);
                command.Parameters.AddWithValue("description", NpgsqlDbType.Text, (object?)role.Description ?? DBNull.Value);
                command.Parameters.AddWithValue("is_built_in", NpgsqlDbType.Boolean, role.IsBuiltIn);
                command.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, role.CreatedAt);
                command.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, role.UpdatedAt);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await ReplacePermissionsAsync(connection.Connection, transaction, role.RoleId, role.Permissions, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Role with ID '{role.RoleId}' or name '{role.Name}' already exists.", ex);
        }

        return role;
    }

    /// <inheritdoc />
    public async Task<RoleDefinition?> UpdateRoleAsync(RoleDefinition role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(role);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Built-in roles' name/description may be updated, but the is_built_in
        // flag is immutable so a built-in role can never be demoted to deletable.
        var updateRole = $"""
            UPDATE {_rolesTable}
            SET name = @name, description = @description, updated_at = NOW()
            WHERE role_id = @role_id
            RETURNING created_at, is_built_in
            """;

        DateTimeOffset createdAt;
        bool isBuiltIn;

        await using (var command = new NpgsqlCommand(updateRole, connection.Connection, transaction))
        {
            command.Parameters.AddWithValue("role_id", NpgsqlDbType.Uuid, role.RoleId);
            command.Parameters.AddWithValue("name", NpgsqlDbType.Varchar, role.Name);
            command.Parameters.AddWithValue("description", NpgsqlDbType.Text, (object?)role.Description ?? DBNull.Value);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await reader.DisposeAsync().ConfigureAwait(false);
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            createdAt = reader.GetFieldValue<DateTimeOffset>(0);
            isBuiltIn = reader.GetBoolean(1);
        }

        await ReplacePermissionsAsync(connection.Connection, transaction, role.RoleId, role.Permissions, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new RoleDefinition
        {
            RoleId = role.RoleId,
            Name = role.Name,
            Description = role.Description,
            IsBuiltIn = isBuiltIn,
            Permissions = role.Permissions,
            CreatedAt = createdAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <inheritdoc />
    public async Task<bool> DeleteRoleAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        // Built-in roles are protected: the DELETE only affects non-built-in
        // rows, so a built-in id deletes 0 rows and returns false.
        var sql = $"DELETE FROM {_rolesTable} WHERE role_id = @role_id AND is_built_in = FALSE";

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("role_id", NpgsqlDbType.Uuid, roleId);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PermissionGrant>> GetPermissionsAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadPermissionsAsync(connection.Connection, transaction: null, roleId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PermissionGrant>> SetPermissionsAsync(
        Guid roleId,
        IReadOnlyList<PermissionGrant> permissions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // No-op (return empty) when the role does not exist, matching the
        // in-memory store's contract.
        var exists = $"SELECT 1 FROM {_rolesTable} WHERE role_id = @role_id";
        await using (var existsCommand = new NpgsqlCommand(exists, connection.Connection, transaction))
        {
            existsCommand.Parameters.AddWithValue("role_id", NpgsqlDbType.Uuid, roleId);
            var found = await existsCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (found is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return [];
            }
        }

        await ReplacePermissionsAsync(connection.Connection, transaction, roleId, permissions, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return permissions;
    }

    /// <inheritdoc />
    public async Task<EffectivePermissions> GetEffectivePermissionsAsync(
        string userId,
        IReadOnlyList<string> roles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roles);

        if (roles.Count == 0)
        {
            return new EffectivePermissions
            {
                UserId = userId ?? string.Empty,
                Roles = roles,
                Permissions = [],
            };
        }

        // Resolve grants across every role whose name matches one of the
        // supplied roles (case-insensitive), de-duplicated. A single set-based
        // query keeps this correct and cheap under concurrent writes.
        var sql = $"""
            SELECT DISTINCT p.service, p.layer, p.operation
            FROM {_rolesTable} r
            JOIN {_permissionsTable} p ON p.role_id = r.role_id
            WHERE LOWER(r.name) = ANY(@role_names)
            """;

        var loweredNames = roles
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray();

        var grants = new List<PermissionGrant>();

        if (loweredNames.Length > 0)
        {
            await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("role_names", NpgsqlDbType.Array | NpgsqlDbType.Text, loweredNames);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                grants.Add(new PermissionGrant
                {
                    Service = reader.GetString(0),
                    Layer = reader.GetString(1),
                    Operation = reader.GetString(2),
                });
            }
        }

        return new EffectivePermissions
        {
            UserId = userId ?? string.Empty,
            Roles = roles,
            Permissions = grants,
        };
    }

    private async Task<IReadOnlyList<RoleDefinition>> ReadRolesAsync(
        NpgsqlConnection connection,
        Guid? roleId,
        CancellationToken cancellationToken)
    {
        var filter = roleId.HasValue ? "WHERE role_id = @role_id" : string.Empty;
        var sql = $"""
            SELECT role_id, name, description, is_built_in, created_at, updated_at
            FROM {_rolesTable}
            {filter}
            ORDER BY name
            """;

        var roles = new List<RoleDefinition>();
        var permissionsByRole = new Dictionary<Guid, List<PermissionGrant>>();

        await using (var command = new NpgsqlCommand(sql, connection))
        {
            if (roleId.HasValue)
            {
                command.Parameters.AddWithValue("role_id", NpgsqlDbType.Uuid, roleId.Value);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetGuid(0);
                permissionsByRole[id] = [];
                roles.Add(new RoleDefinition
                {
                    RoleId = id,
                    Name = reader.GetString(1),
                    Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                    IsBuiltIn = reader.GetBoolean(3),
                    CreatedAt = reader.GetFieldValue<DateTimeOffset>(4),
                    UpdatedAt = reader.GetFieldValue<DateTimeOffset>(5),
                    Permissions = [],
                });
            }
        }

        if (roles.Count == 0)
        {
            return roles;
        }

        var permissionFilter = roleId.HasValue ? "WHERE role_id = @role_id" : string.Empty;
        var permissionSql = $"""
            SELECT role_id, service, layer, operation
            FROM {_permissionsTable}
            {permissionFilter}
            """;

        await using (var permissionCommand = new NpgsqlCommand(permissionSql, connection))
        {
            if (roleId.HasValue)
            {
                permissionCommand.Parameters.AddWithValue("role_id", NpgsqlDbType.Uuid, roleId.Value);
            }

            await using var reader = await permissionCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetGuid(0);
                if (permissionsByRole.TryGetValue(id, out var list))
                {
                    list.Add(new PermissionGrant
                    {
                        Service = reader.GetString(1),
                        Layer = reader.GetString(2),
                        Operation = reader.GetString(3),
                    });
                }
            }
        }

        return roles
            .Select(role => new RoleDefinition
            {
                RoleId = role.RoleId,
                Name = role.Name,
                Description = role.Description,
                IsBuiltIn = role.IsBuiltIn,
                CreatedAt = role.CreatedAt,
                UpdatedAt = role.UpdatedAt,
                Permissions = permissionsByRole[role.RoleId],
            })
            .ToList();
    }

    private async Task<IReadOnlyList<PermissionGrant>> ReadPermissionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid roleId,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT service, layer, operation
            FROM {_permissionsTable}
            WHERE role_id = @role_id
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("role_id", NpgsqlDbType.Uuid, roleId);

        var grants = new List<PermissionGrant>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            grants.Add(new PermissionGrant
            {
                Service = reader.GetString(0),
                Layer = reader.GetString(1),
                Operation = reader.GetString(2),
            });
        }

        return grants;
    }

    private async Task ReplacePermissionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid roleId,
        IReadOnlyList<PermissionGrant> permissions,
        CancellationToken cancellationToken)
    {
        var delete = $"DELETE FROM {_permissionsTable} WHERE role_id = @role_id";
        await using (var deleteCommand = new NpgsqlCommand(delete, connection, transaction))
        {
            deleteCommand.Parameters.AddWithValue("role_id", NpgsqlDbType.Uuid, roleId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (permissions.Count == 0)
        {
            return;
        }

        var insert = $"""
            INSERT INTO {_permissionsTable} (role_id, service, layer, operation)
            VALUES (@role_id, @service, @layer, @operation)
            ON CONFLICT (role_id, service, layer, operation) DO NOTHING
            """;

        foreach (var grant in permissions)
        {
            await using var command = new NpgsqlCommand(insert, connection, transaction);
            command.Parameters.AddWithValue("role_id", NpgsqlDbType.Uuid, roleId);
            command.Parameters.AddWithValue("service", NpgsqlDbType.Varchar, grant.Service);
            command.Parameters.AddWithValue("layer", NpgsqlDbType.Varchar, grant.Layer);
            command.Parameters.AddWithValue("operation", NpgsqlDbType.Varchar, grant.Operation);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

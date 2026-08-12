// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Identity.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Identity;

/// <summary>
/// PostgreSQL-backed managed-user store (#3141). Persists user identities, their role
/// assignments, and activation state to the <c>honua.managed_users</c> /
/// <c>honua.managed_user_roles</c> tables created by migration 106, so managed-identity
/// membership survives process restart and is shared across scaled nodes. Backs both the
/// admin <see cref="IUserStore"/> surface and the SCIM 2.0 provisioning surface
/// (<see cref="IScimUserStore"/>, #510) over the same record set, replacing the
/// process-local <c>InMemoryUserStore</c> as the registered implementation on Postgres
/// profiles.
/// </summary>
/// <remarks>
/// Identifier resolution (finding 2 of #3141): administrative <see cref="GetUserAsync"/>
/// calls prefer the record id (the SCIM <c>userName</c>), while authentication membership
/// calls use <see cref="GetUserByPrincipalIdAsync"/> and match the indexed, case-sensitive
/// pair of <c>external_issuer</c> plus <c>external_id</c> (the validated OIDC issuer and
/// subject). The separate namespace prevents both record-id and cross-issuer subject
/// collisions from resolving security membership to the wrong user. Because this store is
/// shared, a resolution miss is authoritative — the #3119 fail-closed managed-membership
/// marker remains as defense in depth for identifier drift and store outages.
/// </remarks>
internal sealed class PostgresUserStore : IUserStore, IScimUserStore
{
    private const string UserColumns =
        "u.user_id, u.external_id, u.external_issuer, u.display_name, u.email, u.provisioning_source, u.provider_id, u.is_active, u.created_at, u.updated_at";

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _usersTable;
    private readonly string _rolesTable;
    private readonly string _groupsTable;
    private readonly string _groupMembersTable;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresUserStore"/> class.
    /// </summary>
    /// <param name="dataSource">The Npgsql data source.</param>
    /// <param name="schemaName">Optional schema override (used by tests for isolated
    /// schemas); defaults to the application schema.</param>
    public PostgresUserStore(NpgsqlDataSource dataSource, string? schemaName = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
        _usersTable = SchemaSearchPath.QualifyTable("managed_users", schemaName);
        _rolesTable = SchemaSearchPath.QualifyTable("managed_user_roles", schemaName);
        _groupsTable = SchemaSearchPath.QualifyTable("scim_groups", schemaName);
        _groupMembersTable = SchemaSearchPath.QualifyTable("scim_group_members", schemaName);
    }

    /// <summary>
    /// Seeds (upserts) a user into the store, replacing activation state and roles (for
    /// testing — the durable counterpart of <c>InMemoryUserStore.Seed</c>, so fixture-based
    /// integration tests can restore a deterministic baseline before each test against the
    /// shared <c>honua.managed_users</c> record set).
    /// </summary>
    internal async Task SeedAsync(ManagedUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var upsert = $"""
            INSERT INTO {_usersTable} (user_id, external_id, external_issuer, display_name, email, provisioning_source, provider_id, is_active, created_at, updated_at)
            VALUES (@user_id, @external_id, @external_issuer, @display_name, @email, @provisioning_source, @provider_id, @is_active, @created_at, @updated_at)
            ON CONFLICT (user_id) DO UPDATE SET
                external_id = EXCLUDED.external_id,
                external_issuer = EXCLUDED.external_issuer,
                display_name = EXCLUDED.display_name,
                email = EXCLUDED.email,
                provisioning_source = EXCLUDED.provisioning_source,
                provider_id = EXCLUDED.provider_id,
                is_active = EXCLUDED.is_active,
                updated_at = EXCLUDED.updated_at
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var command = new NpgsqlCommand(upsert, connection, transaction))
        {
            command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, user.UserId);
            command.Parameters.AddWithValue("external_id", NpgsqlDbType.Varchar, (object?)user.ExternalId ?? DBNull.Value);
            command.Parameters.AddWithValue("external_issuer", NpgsqlDbType.Varchar, (object?)NormalizeIssuer(user.ExternalIssuer) ?? DBNull.Value);
            command.Parameters.AddWithValue("display_name", NpgsqlDbType.Varchar, user.DisplayName);
            command.Parameters.AddWithValue("email", NpgsqlDbType.Varchar, (object?)user.Email ?? DBNull.Value);
            command.Parameters.AddWithValue("provisioning_source", NpgsqlDbType.Varchar, user.ProvisioningSource);
            command.Parameters.AddWithValue("provider_id", NpgsqlDbType.Uuid, (object?)user.ProviderId ?? DBNull.Value);
            command.Parameters.AddWithValue("is_active", NpgsqlDbType.Boolean, user.IsActive);
            command.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, user.CreatedAt);
            command.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, user.UpdatedAt);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ReplaceRolesAsync(connection, transaction, user.UserId, NormalizeRoles(user.Roles), cancellationToken).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---- IUserStore ------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<UserListResult> ListUsersAsync(UserListFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var conditions = new List<string>();
        var parameters = new List<NpgsqlParameter>();

        if (!string.IsNullOrEmpty(filter.ProvisioningSource))
        {
            conditions.Add("LOWER(u.provisioning_source) = LOWER(@provisioning_source)");
            parameters.Add(new NpgsqlParameter("provisioning_source", NpgsqlDbType.Varchar) { Value = filter.ProvisioningSource });
        }

        if (!string.IsNullOrEmpty(filter.Role))
        {
            conditions.Add($"EXISTS (SELECT 1 FROM {_rolesTable} rf WHERE rf.user_id = u.user_id AND LOWER(rf.role) = LOWER(@role_filter))");
            parameters.Add(new NpgsqlParameter("role_filter", NpgsqlDbType.Varchar) { Value = filter.Role });
        }

        if (filter.IsActive.HasValue)
        {
            conditions.Add("u.is_active = @is_active");
            parameters.Add(new NpgsqlParameter("is_active", NpgsqlDbType.Boolean) { Value = filter.IsActive.Value });
        }

        var where = conditions.Count == 0 ? string.Empty : $"WHERE {string.Join(" AND ", conditions)}";

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            int totalCount;
            var countSql = $"SELECT COUNT(*) FROM {_usersTable} u {where}";
            await using (var countCommand = new NpgsqlCommand(countSql, connection))
            {
                countCommand.Parameters.AddRange(parameters.Select(static p => p.Clone()).ToArray());
                totalCount = Convert.ToInt32(
                    await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            var pageSql = $"""
                {SelectUsersSql(where)}
                ORDER BY LOWER(u.user_id)
                LIMIT @limit OFFSET @offset
                """;

            var users = new List<ManagedUser>();
            await using (var pageCommand = new NpgsqlCommand(pageSql, connection))
            {
                pageCommand.Parameters.AddRange(parameters.Select(static p => p.Clone()).ToArray());
                pageCommand.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, Math.Max(0, filter.Limit));
                pageCommand.Parameters.AddWithValue("offset", NpgsqlDbType.Integer, Math.Max(0, filter.Offset));

                await using var reader = await pageCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    users.Add(ReadUser(reader));
                }
            }

            return new UserListResult { Users = users, TotalCount = totalCount };
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // Fresh/legacy DB where migration 106 has not created the managed-user tables:
            // no managed users exist (honua-server#1341 resilience pattern).
            return new UserListResult { Users = [], TotalCount = 0 };
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Resolves <paramref name="userId"/> against the record id (SCIM <c>userName</c>)
    /// first, then an unambiguous exact external subject (<c>external_id</c>).
    /// Authentication membership uses the dedicated issuer-scoped principal lookup below.
    /// </remarks>
    public Task<ManagedUser?> GetUserAsync(string userId, CancellationToken cancellationToken = default)
        => GetUserByIdentifierAsync(userId, cancellationToken);

    /// <inheritdoc />
    public async Task<ManagedUser?> GetUserByPrincipalIdAsync(
        string principalId,
        string? issuer = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(principalId))
        {
            return null;
        }

        var normalizedIssuer = NormalizeIssuer(issuer);
        var where = normalizedIssuer is null
            ? "WHERE u.external_id = @id AND u.external_issuer IS NULL OR LOWER(u.user_id) = LOWER(@id)"
            : "WHERE u.external_id = @id AND u.external_issuer = @issuer";
        var sql = $"""
            {SelectUsersSql(where)}
            ORDER BY (u.external_id = @id) DESC
            LIMIT 1
            """;

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("id", NpgsqlDbType.Varchar, principalId);
            if (normalizedIssuer is not null)
            {
                command.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, normalizedIssuer);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadUser(reader) : null;
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
        {
            return null;
        }
    }

    private async Task<ManagedUser?> GetUserByIdentifierAsync(
        string identifier,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        var where = "WHERE LOWER(u.user_id) = LOWER(@id) "
            + $"OR (u.external_id = @id AND 1 = (SELECT COUNT(*) FROM {_usersTable} x WHERE x.external_id = @id))";
        var sql = $"""
            {SelectUsersSql(where)}
            ORDER BY (LOWER(u.user_id) = LOWER(@id)) DESC
            LIMIT 1
            """;

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("id", NpgsqlDbType.Varchar, identifier);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadUser(reader) : null;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<ManagedUser?> UpdateUserRolesAsync(string userId, IReadOnlyList<string> roles, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roles);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var canonicalId = await ResolveCanonicalUserIdAsync(connection, transaction, userId, cancellationToken).ConfigureAwait(false);
        if (canonicalId is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        await ReplaceRolesAsync(connection, transaction, canonicalId, NormalizeRoles(roles), cancellationToken).ConfigureAwait(false);
        await TouchUserAsync(connection, transaction, canonicalId, cancellationToken).ConfigureAwait(false);

        var updated = await ReadUserByCanonicalIdAsync(connection, transaction, canonicalId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    /// <inheritdoc />
    public async Task<bool> DeprovisionUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var canonicalId = await ResolveCanonicalUserIdAsync(connection, transaction, userId, cancellationToken).ConfigureAwait(false);
        if (canonicalId is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await DeactivateAsync(connection, transaction, canonicalId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    // ---- IScimUserStore (#510) -------------------------------------------------------

    /// <inheritdoc />
    public async Task<ManagedUser?> CreateUserAsync(ScimUserProvisioning provisioning, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provisioning);

        // SCIM userName is the IdP-owned, unique login identifier; reuse it as the stable
        // user id so re-provisioning is idempotent on the same key (in-memory store parity).
        // A conflicting userName or externalId is reported to the caller (SCIM 409) rather
        // than silently overwriting — the unique indexes enforce it under concurrency.
        var now = DateTimeOffset.UtcNow;
        var userId = provisioning.UserName.Trim();
        var roles = NormalizeRoles(provisioning.Roles);
        var externalId = string.IsNullOrWhiteSpace(provisioning.ExternalId) ? null : provisioning.ExternalId.Trim();
        var externalIssuer = externalId is null ? null : NormalizeIssuer(provisioning.ExternalIssuer);

        var insert = $"""
            INSERT INTO {_usersTable} (user_id, external_id, external_issuer, display_name, email, provisioning_source, provider_id, is_active, created_at, updated_at)
            VALUES (@user_id, @external_id, @external_issuer, @display_name, @email, 'scim', NULL, @is_active, @created_at, @updated_at)
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using (var command = new NpgsqlCommand(insert, connection, transaction))
            {
                command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, userId);
                command.Parameters.AddWithValue("external_id", NpgsqlDbType.Varchar, (object?)externalId ?? DBNull.Value);
                command.Parameters.AddWithValue("external_issuer", NpgsqlDbType.Varchar, (object?)externalIssuer ?? DBNull.Value);
                command.Parameters.AddWithValue("display_name", NpgsqlDbType.Varchar,
                    string.IsNullOrWhiteSpace(provisioning.DisplayName) ? userId : provisioning.DisplayName);
                command.Parameters.AddWithValue("email", NpgsqlDbType.Varchar, (object?)provisioning.Email ?? DBNull.Value);
                command.Parameters.AddWithValue("is_active", NpgsqlDbType.Boolean, provisioning.Active);
                command.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, now);
                command.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, now);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var groupRoles = await ReadGroupRolesForUserAsync(connection, transaction, userId, cancellationToken).ConfigureAwait(false);
            await ReplaceRolesAsync(
                connection,
                transaction,
                userId,
                NormalizeRoles([.. roles, .. groupRoles]),
                cancellationToken).ConfigureAwait(false);
            var created = await ReadUserByCanonicalIdAsync(connection, transaction, userId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
            return created;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<ManagedUser?> FindByUserNameAsync(string userName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return null;
        }

        var sql = $"""
            {SelectUsersSql("WHERE LOWER(u.user_id) = LOWER(@user_name)")}
            LIMIT 1
            """;

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("user_name", NpgsqlDbType.Varchar, userName.Trim());

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadUser(reader) : null;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<ScimUserPage> ListUsersAsync(ScimUserQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var conditions = new List<string> { "LOWER(u.provisioning_source) = 'scim'" };
        var parameters = new List<NpgsqlParameter>();

        if (!string.IsNullOrEmpty(query.UserNameEquals))
        {
            conditions.Add("LOWER(u.user_id) = LOWER(@user_name)");
            parameters.Add(new NpgsqlParameter("user_name", NpgsqlDbType.Varchar) { Value = query.UserNameEquals });
        }

        var where = $"WHERE {string.Join(" AND ", conditions)}";

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            int totalCount;
            var countSql = $"SELECT COUNT(*) FROM {_usersTable} u {where}";
            await using (var countCommand = new NpgsqlCommand(countSql, connection))
            {
                countCommand.Parameters.AddRange(parameters.Select(static p => p.Clone()).ToArray());
                totalCount = Convert.ToInt32(
                    await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            var pageSql = $"""
                {SelectUsersSql(where)}
                ORDER BY LOWER(u.user_id)
                LIMIT @limit OFFSET @offset
                """;

            var users = new List<ManagedUser>();
            await using (var pageCommand = new NpgsqlCommand(pageSql, connection))
            {
                pageCommand.Parameters.AddRange(parameters.Select(static p => p.Clone()).ToArray());
                pageCommand.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, Math.Max(0, query.Count));
                pageCommand.Parameters.AddWithValue("offset", NpgsqlDbType.Integer, Math.Max(0, query.StartIndex - 1));

                await using var reader = await pageCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    users.Add(ReadUser(reader));
                }
            }

            return new ScimUserPage { Users = users, TotalCount = totalCount };
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return new ScimUserPage { Users = [], TotalCount = 0 };
        }
    }

    /// <inheritdoc />
    public async Task<ManagedUser?> ReplaceUserAsync(string userId, ScimUserProvisioning provisioning, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provisioning);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var canonicalId = await ResolveCanonicalUserIdAsync(connection, transaction, userId, cancellationToken).ConfigureAwait(false);
        if (canonicalId is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        // The record id (userName) is immutable, matching the in-memory store. external_id
        // is only overwritten when the IdP supplies one: losing the stable subject on a PUT
        // that omits it would orphan in-flight deferred snapshots keyed by that subject.
        var update = $"""
            UPDATE {_usersTable}
            SET display_name = @display_name,
                email = @email,
                is_active = @is_active,
                external_id = COALESCE(@external_id, external_id),
                external_issuer = CASE WHEN @external_id IS NULL THEN external_issuer ELSE @external_issuer END,
                updated_at = NOW()
            WHERE user_id = @user_id
            """;

        try
        {
            await using (var command = new NpgsqlCommand(update, connection, transaction))
            {
                var externalId = string.IsNullOrWhiteSpace(provisioning.ExternalId) ? null : provisioning.ExternalId.Trim();
                command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, canonicalId);
                command.Parameters.AddWithValue("display_name", NpgsqlDbType.Varchar,
                    string.IsNullOrWhiteSpace(provisioning.DisplayName) ? provisioning.UserName : provisioning.DisplayName);
                command.Parameters.AddWithValue("email", NpgsqlDbType.Varchar, (object?)provisioning.Email ?? DBNull.Value);
                command.Parameters.AddWithValue("is_active", NpgsqlDbType.Boolean, provisioning.Active);
                command.Parameters.AddWithValue("external_id", NpgsqlDbType.Varchar, (object?)externalId ?? DBNull.Value);
                command.Parameters.AddWithValue(
                    "external_issuer",
                    NpgsqlDbType.Varchar,
                    (object?)NormalizeIssuer(provisioning.ExternalIssuer) ?? DBNull.Value);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await ReplaceRolesAsync(connection, transaction, canonicalId, NormalizeRoles(provisioning.Roles), cancellationToken).ConfigureAwait(false);
            var replaced = await ReadUserByCanonicalIdAsync(connection, transaction, canonicalId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
            return replaced;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Another user already owns the supplied externalId. Translate rather than leak
            // a raw PostgresException to the SCIM surface.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Another user already has externalId '{provisioning.ExternalId}'.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<ManagedUser?> SetActiveAsync(string userId, bool active, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var canonicalId = await ResolveCanonicalUserIdAsync(connection, transaction, userId, cancellationToken).ConfigureAwait(false);
        if (canonicalId is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (active)
        {
            var update = $"UPDATE {_usersTable} SET is_active = TRUE, updated_at = NOW() WHERE user_id = @user_id";
            await using var command = new NpgsqlCommand(update, connection, transaction);
            command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, canonicalId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            // Group memberships intentionally survive deprovisioning. Restore their mapped
            // roles when the user is reactivated so the durable group view and user role view
            // cannot diverge after delete/reprovision flows.
            var groupRoles = await ReadGroupRolesForUserAsync(connection, transaction, canonicalId, cancellationToken).ConfigureAwait(false);
            await ReplaceRolesAsync(connection, transaction, canonicalId, groupRoles, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await DeactivateAsync(connection, transaction, canonicalId, cancellationToken).ConfigureAwait(false);
        }

        var updated = await ReadUserByCanonicalIdAsync(connection, transaction, canonicalId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    /// <inheritdoc />
    public Task<bool> DeleteUserAsync(string userId, CancellationToken cancellationToken = default)
        => DeprovisionUserAsync(userId, cancellationToken);

    // ---- helpers -----------------------------------------------------------------------

    private string SelectUsersSql(string where) => $"""
        SELECT {UserColumns},
               COALESCE(ARRAY_AGG(r.role ORDER BY LOWER(r.role)) FILTER (WHERE r.role IS NOT NULL), ARRAY[]::VARCHAR[]) AS roles
        FROM {_usersTable} u
        LEFT JOIN {_rolesTable} r ON r.user_id = u.user_id
        {where}
        GROUP BY u.user_id
        """;

    private static ManagedUser ReadUser(NpgsqlDataReader reader) => new()
    {
        UserId = reader.GetString(0),
        ExternalId = reader.IsDBNull(1) ? null : reader.GetString(1),
        ExternalIssuer = reader.IsDBNull(2) ? null : reader.GetString(2),
        DisplayName = reader.GetString(3),
        Email = reader.IsDBNull(4) ? null : reader.GetString(4),
        ProvisioningSource = reader.GetString(5),
        ProviderId = reader.IsDBNull(6) ? null : reader.GetGuid(6),
        IsActive = reader.GetBoolean(7),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(8),
        UpdatedAt = reader.GetFieldValue<DateTimeOffset>(9),
        Roles = reader.GetFieldValue<string[]>(10),
    };

    /// <summary>
    /// Resolves the canonical record id for a caller-supplied identifier: exact record id
    /// (SCIM <c>userName</c>) wins over an exact external-subject match.
    /// Returns <see langword="null"/> when no record matches or the tables do not exist.
    /// </summary>
    private async Task<string?> ResolveCanonicalUserIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string userId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        var sql = $"""
            SELECT user_id FROM {_usersTable}
            WHERE LOWER(user_id) = LOWER(@id)
               OR (external_id = @id AND 1 = (SELECT COUNT(*) FROM {_usersTable} x WHERE x.external_id = @id))
            ORDER BY (LOWER(user_id) = LOWER(@id)) DESC
            LIMIT 1
            """;

        try
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("id", NpgsqlDbType.Varchar, userId);
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return null;
        }
    }

    private async Task<ManagedUser?> ReadUserByCanonicalIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string canonicalId,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            {SelectUsersSql("WHERE u.user_id = @user_id")}
            LIMIT 1
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, canonicalId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadUser(reader) : null;
    }

    private async Task<List<string>> ReadGroupRolesForUserAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string canonicalId,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT DISTINCT g.display_name
            FROM {_groupMembersTable} m
            INNER JOIN {_groupsTable} g ON g.group_id = m.group_id
            WHERE LOWER(m.user_id) = LOWER(@user_id)
            ORDER BY g.display_name
            """;

        var roles = new List<string>();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, canonicalId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            roles.Add(reader.GetString(0));
        }

        return roles;
    }

    private async Task ReplaceRolesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string canonicalId,
        List<string> roles,
        CancellationToken cancellationToken)
    {
        var delete = $"DELETE FROM {_rolesTable} WHERE user_id = @user_id";
        await using (var deleteCommand = new NpgsqlCommand(delete, connection, transaction))
        {
            deleteCommand.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, canonicalId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (roles.Count == 0)
        {
            return;
        }

        var insert = $"""
            INSERT INTO {_rolesTable} (user_id, role)
            VALUES (@user_id, @role)
            ON CONFLICT (user_id, LOWER(role)) DO NOTHING
            """;

        foreach (var role in roles)
        {
            await using var command = new NpgsqlCommand(insert, connection, transaction);
            command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, canonicalId);
            command.Parameters.AddWithValue("role", NpgsqlDbType.Varchar, role);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TouchUserAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string canonicalId,
        CancellationToken cancellationToken)
    {
        var sql = $"UPDATE {_usersTable} SET updated_at = NOW() WHERE user_id = @user_id";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, canonicalId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deactivates a user and revokes every role — deactivation must deterministically stop
    /// deferred work authorized by this identity on any replica (#3141 acceptance 1).
    /// </summary>
    private async Task DeactivateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string canonicalId,
        CancellationToken cancellationToken)
    {
        var update = $"UPDATE {_usersTable} SET is_active = FALSE, updated_at = NOW() WHERE user_id = @user_id";
        await using (var command = new NpgsqlCommand(update, connection, transaction))
        {
            command.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, canonicalId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var deleteRoles = $"DELETE FROM {_rolesTable} WHERE user_id = @user_id";
        await using (var deleteCommand = new NpgsqlCommand(deleteRoles, connection, transaction))
        {
            deleteCommand.Parameters.AddWithValue("user_id", NpgsqlDbType.Varchar, canonicalId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static List<string> NormalizeRoles(IReadOnlyList<string> roles)
        => roles
            .Where(static r => !string.IsNullOrWhiteSpace(r))
            .Select(static r => r.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? NormalizeIssuer(string? issuer)
        => string.IsNullOrWhiteSpace(issuer) ? null : issuer.Trim();
}

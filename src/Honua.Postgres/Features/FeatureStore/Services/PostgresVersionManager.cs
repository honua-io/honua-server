// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Postgres branch-versioning manager (#1272 Track B, ADR-0051; production hardening #1553). Owns
/// gdb-version lifecycle over <c>honua.gdb_versions</c> + the overlay <c>honua.version_edits</c>, plus the
/// reconcile/post merge workflow with #371 conflict resolution. Reconcile/post and a conflicting resolve
/// run under a durable (service, version) <see cref="IVersionLock"/> so concurrent maintenance for the
/// same version is serialized; large runs execute through the canonical async job runner. A null or
/// DEFAULT resolution always maps to the byte-identical base path.
/// </summary>
internal sealed partial class PostgresVersionManager : IVersionManager
{
    private const string DefaultVersionSentinel = "sde.default";

    // The (service, version) maintenance lock auto-expires after this lease if a holder crashes
    // mid-reconcile/post, so a stuck lock can never permanently wedge a version (#1553). The Redis
    // handle renews the lease while the critical section runs.
    private static readonly TimeSpan MaintenanceLockLease = TimeSpan.FromMinutes(15);

    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string? _schemaName;
    private readonly IVersionLock _versionLock;
    private readonly string _lockScope;

    /// <summary>Initializes the manager with the database connection provider.</summary>
    /// <param name="connectionProvider">Database connection provider.</param>
    /// <param name="schemaName">Optional schema hosting the DEFAULT <c>features</c> table; null for the search-path default.</param>
    /// <param name="versionLock">
    /// Durable (service, version) lock that serializes reconcile/post/resolve for a version (#1553).
    /// Defaults to a single-node in-process lock when none is supplied (development/test and single-node
    /// deployments); the Redis-backed lock is injected in multi-replica deployments.
    /// </param>
    public PostgresVersionManager(
        IDatabaseConnectionProvider connectionProvider,
        string? schemaName = null,
        IVersionLock? versionLock = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _schemaName = schemaName;
        _versionLock = versionLock ?? new InProcessVersionLock();
        // The version registry is scoped to the schema, so the schema name is the natural lock service
        // scope; the version id makes the key globally unique within it.
        _lockScope = string.IsNullOrWhiteSpace(schemaName) ? "honua.versioning" : schemaName!;
    }

    /// <inheritdoc />
    public bool SupportsVersioning => true;

    /// <inheritdoc />
    public async Task<GdbVersion> CreateAsync(CreateVersionRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.VersionName))
        {
            throw new ArgumentException("Version name is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Owner))
        {
            throw new ArgumentException("Version owner is required.", nameof(request));
        }

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);

        // The merge base is the current DEFAULT generation; reconcile pulls DEFAULT changes since this
        // cursor into the branch. last_value mirrors PostgresChangeTracker.GetCurrentGenerationAsync.
        const string sql = """
            INSERT INTO honua.gdb_versions (version_name, owner, parent_version, access, state, common_ancestor_gen, branch_gen, description)
            VALUES (@name, @owner, @parent, @access, 0, (SELECT last_value FROM honua.sync_generation), (SELECT last_value FROM honua.sync_generation), @description)
            RETURNING version_id, version_name, owner, parent_version, access, state, common_ancestor_gen, branch_gen, description, created_at, modified_at
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("name", request.VersionName);
        command.Parameters.AddWithValue("owner", request.Owner);
        command.Parameters.AddWithValue("parent", (object?)request.ParentVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("access", (short)request.Access);
        command.Parameters.AddWithValue("description", (object?)request.Description ?? DBNull.Value);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Failed to create branch version.");
            }

            return ReadVersion(reader);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException(
                $"A version named '{request.VersionName}' already exists for owner '{request.Owner}'.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        // ON DELETE CASCADE on honua.version_edits removes the overlay rows with the registry row.
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "DELETE FROM honua.gdb_versions WHERE version_id = @id", connection);
        command.Parameters.AddWithValue("id", versionId);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<GdbVersion?> AlterAsync(AlterVersionRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        const string sql = """
            UPDATE honua.gdb_versions
            SET version_name = COALESCE(@name, version_name),
                access = COALESCE(@access, access),
                description = CASE WHEN @description_set THEN @description ELSE description END,
                modified_at = now()
            WHERE version_id = @id
            RETURNING version_id, version_name, owner, parent_version, access, state, common_ancestor_gen, branch_gen, description, created_at, modified_at
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", request.VersionId);
        command.Parameters.AddWithValue("name", (object?)request.VersionName ?? DBNull.Value);
        command.Parameters.AddWithValue("access", request.Access.HasValue ? (short)request.Access.Value : (object)DBNull.Value);
        command.Parameters.AddWithValue("description_set", request.Description is not null);
        command.Parameters.AddWithValue("description", (object?)request.Description ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadVersion(reader);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GdbVersion>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        const string sql = """
            SELECT version_id, version_name, owner, parent_version, access, state, common_ancestor_gen, branch_gen, description, created_at, modified_at
            FROM honua.gdb_versions
            WHERE state <> 3
            ORDER BY created_at
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        var results = ImmutableArray.CreateBuilder<GdbVersion>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadVersion(reader));
        }

        return results.ToImmutable();
    }

    /// <inheritdoc />
    public async Task<VersionContext?> ResolveAsync(string? gdbVersion, CancellationToken cancellationToken = default)
    {
        // Absent/empty or the DEFAULT sentinel resolves to DEFAULT (byte-identical base path).
        if (string.IsNullOrWhiteSpace(gdbVersion) ||
            gdbVersion.Trim().Equals(DefaultVersionSentinel, StringComparison.OrdinalIgnoreCase))
        {
            return VersionContext.Default;
        }

        var version = await FindByIdentityAsync(gdbVersion.Trim(), cancellationToken).ConfigureAwait(false);
        return version is null ? null : VersionContext.ForVersion(version.Value);
    }

    private async Task<GdbVersion?> FindByIdentityAsync(string gdbVersion, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Accept an explicit version-id GUID, or the Esri-style owner.name identity (case-insensitive).
        string sql;
        if (Guid.TryParse(gdbVersion, out var versionId))
        {
            sql = """
                SELECT version_id, version_name, owner, parent_version, access, state, common_ancestor_gen, branch_gen, description, created_at, modified_at
                FROM honua.gdb_versions
                WHERE version_id = @id AND state <> 3
                """;
            await using var byId = new NpgsqlCommand(sql, connection);
            byId.Parameters.AddWithValue("id", versionId);
            return await ReadSingleAsync(byId, cancellationToken).ConfigureAwait(false);
        }

        var (owner, name) = SplitOwnerName(gdbVersion);
        sql = """
            SELECT version_id, version_name, owner, parent_version, access, state, common_ancestor_gen, branch_gen, description, created_at, modified_at
            FROM honua.gdb_versions
            WHERE state <> 3
              AND LOWER(version_name) = LOWER(@name)
              AND (@owner IS NULL OR LOWER(owner) = LOWER(@owner))
            ORDER BY created_at
            LIMIT 1
            """;
        await using var byName = new NpgsqlCommand(sql, connection);
        byName.Parameters.AddWithValue("name", name);
        byName.Parameters.AddWithValue("owner", (object?)owner ?? DBNull.Value);
        return await ReadSingleAsync(byName, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<GdbVersion?> ReadSingleAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadVersion(reader);
    }

    private static (string? Owner, string Name) SplitOwnerName(string gdbVersion)
    {
        // Esri identities are "owner.name"; match on the full name first, but a dotted value also lets us
        // disambiguate by owner. Treat the substring after the last dot as the name and the prefix as owner.
        var lastDot = gdbVersion.LastIndexOf('.');
        if (lastDot <= 0 || lastDot == gdbVersion.Length - 1)
        {
            return (null, gdbVersion);
        }

        return (gdbVersion[..lastDot], gdbVersion[(lastDot + 1)..]);
    }

    private static GdbVersion ReadVersion(NpgsqlDataReader reader) => new()
    {
        VersionId = reader.GetGuid(0),
        VersionName = reader.GetString(1),
        Owner = reader.GetString(2),
        ParentVersion = reader.IsDBNull(3) ? null : reader.GetGuid(3),
        Access = (VersionAccess)reader.GetInt16(4),
        State = (VersionState)reader.GetInt16(5),
        CommonAncestorGeneration = reader.GetInt64(6),
        BranchGeneration = reader.GetInt64(7),
        Description = reader.IsDBNull(8) ? null : reader.GetString(8),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(9),
        ModifiedAt = reader.GetFieldValue<DateTimeOffset>(10),
    };
}

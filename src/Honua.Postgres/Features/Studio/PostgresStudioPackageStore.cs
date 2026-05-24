// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Studio;

internal sealed class PostgresStudioPackageStore : IStudioPackageStore
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string _itemsTable;
    private readonly string _draftsTable;
    private readonly string _versionsTable;
    private readonly string _dependenciesTable;
    private readonly string _publicationRequestsTable;
    private readonly string _rollbackRequestsTable;

    public PostgresStudioPackageStore(IDatabaseConnectionProvider connectionProvider, string? schemaName = null)
    {
        ArgumentNullException.ThrowIfNull(connectionProvider);
        _connectionProvider = connectionProvider;
        _itemsTable = SchemaSearchPath.QualifyTable("studio_content_items", schemaName);
        _draftsTable = SchemaSearchPath.QualifyTable("studio_package_drafts", schemaName);
        _versionsTable = SchemaSearchPath.QualifyTable("studio_content_versions", schemaName);
        _dependenciesTable = SchemaSearchPath.QualifyTable("studio_content_version_dependencies", schemaName);
        _publicationRequestsTable = SchemaSearchPath.QualifyTable("studio_publication_requests", schemaName);
        _rollbackRequestsTable = SchemaSearchPath.QualifyTable("studio_rollback_requests", schemaName);
    }

    public StudioPackagePersistenceMode PersistenceMode => StudioPackagePersistenceMode.Durable;

    public async Task<StudioPackageDraft> CreateDraftAsync(
        StudioPackageDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var envelopeJson = SerializeEnvelope(draft.Envelope);
        var validationJson = SerializeValidation(draft.Validation);
        var family = ToDbFamily(draft.Family);

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        var connection = lease.Connection;
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        var committed = false;
        try
        {
            await UpsertItemAsync(
                connection,
                transaction,
                draft.ItemId,
                draft.PackageKey,
                draft.WorkspaceId,
                draft.Family,
                currentVersionId: null,
                publishedVersionId: null,
                draft.CreatedBy,
                draft.UpdatedBy,
                draft.CreatedAt,
                draft.UpdatedAt,
                cancellationToken).ConfigureAwait(false);

            var sql = $"""
                INSERT INTO {_draftsTable}
                    (draft_id, item_id, package_key, workspace_id, owner_id, family,
                     envelope, validation, base_version_id, generation,
                     created_by, updated_by, created_at, updated_at)
                VALUES
                    (@draft_id, @item_id, @package_key, @workspace_id, @owner_id, @family,
                     @envelope, @validation, @base_version_id, @generation,
                     @created_by, @updated_by, @created_at, @updated_at)
                """;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("@draft_id", draft.DraftId);
            command.Parameters.AddWithValue("@item_id", draft.ItemId);
            command.Parameters.AddWithValue("@package_key", draft.PackageKey);
            command.Parameters.AddWithValue("@workspace_id", (object?)draft.WorkspaceId ?? DBNull.Value);
            command.Parameters.AddWithValue("@owner_id", (object?)draft.OwnerId ?? DBNull.Value);
            command.Parameters.AddWithValue("@family", family);
            command.Parameters.Add(new NpgsqlParameter("@envelope", NpgsqlDbType.Jsonb) { Value = envelopeJson });
            command.Parameters.Add(new NpgsqlParameter("@validation", NpgsqlDbType.Jsonb) { Value = validationJson });
            command.Parameters.AddWithValue("@base_version_id", (object?)draft.BaseVersionId ?? DBNull.Value);
            command.Parameters.AddWithValue("@generation", draft.Generation);
            command.Parameters.AddWithValue("@created_by", (object?)draft.CreatedBy ?? DBNull.Value);
            command.Parameters.AddWithValue("@updated_by", (object?)draft.UpdatedBy ?? DBNull.Value);
            command.Parameters.AddWithValue("@created_at", draft.CreatedAt);
            command.Parameters.AddWithValue("@updated_at", draft.UpdatedAt);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            committed = true;
            return draft;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            if (!committed)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
            throw new InvalidOperationException("Studio package key conflicts with an existing content item.", ex);
        }
        catch
        {
            if (!committed)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
            throw;
        }
    }

    public async Task<StudioPackageDraft?> GetDraftAsync(
        Guid draftId,
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT draft_id, item_id, package_key, workspace_id, owner_id, family,
                   envelope, validation, base_version_id, generation,
                   created_by, updated_by, created_at, updated_at
            FROM {_draftsTable}
            WHERE draft_id = @draft_id
            """;

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, lease.Connection);
        command.Parameters.AddWithValue("@draft_id", draftId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadDraft(reader)
            : null;
    }

    public async Task<StudioPackageDraft?> UpdateDraftAsync(
        StudioPackageDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var envelopeJson = SerializeEnvelope(draft.Envelope);
        var validationJson = SerializeValidation(draft.Validation);
        var sql = $"""
            UPDATE {_draftsTable}
            SET package_key = @package_key,
                workspace_id = @workspace_id,
                owner_id = @owner_id,
                family = @family,
                envelope = @envelope,
                validation = @validation,
                generation = generation + 1,
                updated_by = @updated_by,
                updated_at = @updated_at
            WHERE draft_id = @draft_id
              AND generation = @generation
            RETURNING generation
            """;

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await lease.Connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        var committed = false;
        try
        {
            await using var command = new NpgsqlCommand(sql, lease.Connection, transaction);
            command.Parameters.AddWithValue("@draft_id", draft.DraftId);
            command.Parameters.AddWithValue("@package_key", draft.PackageKey);
            command.Parameters.AddWithValue("@workspace_id", (object?)draft.WorkspaceId ?? DBNull.Value);
            command.Parameters.AddWithValue("@owner_id", (object?)draft.OwnerId ?? DBNull.Value);
            command.Parameters.AddWithValue("@family", ToDbFamily(draft.Family));
            command.Parameters.Add(new NpgsqlParameter("@envelope", NpgsqlDbType.Jsonb) { Value = envelopeJson });
            command.Parameters.Add(new NpgsqlParameter("@validation", NpgsqlDbType.Jsonb) { Value = validationJson });
            command.Parameters.AddWithValue("@updated_by", (object?)draft.UpdatedBy ?? DBNull.Value);
            command.Parameters.AddWithValue("@updated_at", draft.UpdatedAt);
            command.Parameters.AddWithValue("@generation", draft.Generation);

            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                var exists = await DraftExistsAsync(lease.Connection, transaction, draft.DraftId, cancellationToken).ConfigureAwait(false);
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                if (exists)
                {
                    throw new InvalidOperationException("Stale draft generation; refresh and retry.");
                }

                return null;
            }

            await UpsertItemAsync(
                lease.Connection,
                transaction,
                draft.ItemId,
                draft.PackageKey,
                draft.WorkspaceId,
                draft.Family,
                currentVersionId: null,
                publishedVersionId: null,
                draft.CreatedBy,
                draft.UpdatedBy,
                draft.CreatedAt,
                draft.UpdatedAt,
                cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            committed = true;
            return await GetDraftAsync(draft.DraftId, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            if (!committed)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
            throw new InvalidOperationException("Studio package key conflicts with an existing content item.", ex);
        }
        catch
        {
            if (!committed)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
            throw;
        }
    }

    public async Task<bool> DeleteDraftAsync(Guid draftId, CancellationToken cancellationToken = default)
    {
        var sql = $"DELETE FROM {_draftsTable} WHERE draft_id = @draft_id";
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, lease.Connection);
        command.Parameters.AddWithValue("@draft_id", draftId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task<StudioContentVersion> CreateVersionAsync(
        StudioPackageDraft draft,
        string? changeNote,
        string? actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var createdAt = DateTimeOffset.UtcNow;
        var envelopeJson = SerializeEnvelope(draft.Envelope);
        var validationJson = SerializeValidation(draft.Validation);
        var dependenciesJson = JsonSerializer.Serialize(
            draft.Envelope.Dependencies.ToArray(),
            StudioJsonContext.Default.StudioPackageDependencyArray);
        var provenanceJson = JsonSerializer.Serialize(
            draft.Envelope.Provenance.ToArray(),
            StudioJsonContext.Default.StudioProvenanceRefArray);
        var contentHash = StudioPackageHash.ComputeJson(envelopeJson);
        var versionId = Guid.NewGuid();

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        var connection = lease.Connection;
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        var committed = false;
        try
        {
            if (!await DraftExistsAsync(connection, transaction, draft.DraftId, cancellationToken).ConfigureAwait(false))
            {
                throw new KeyNotFoundException("Studio package draft was not found.");
            }

            await UpsertItemAsync(
                connection,
                transaction,
                draft.ItemId,
                draft.PackageKey,
                draft.WorkspaceId,
                draft.Family,
                currentVersionId: null,
                publishedVersionId: null,
                draft.CreatedBy,
                actorId,
                draft.CreatedAt,
                createdAt,
                cancellationToken).ConfigureAwait(false);

            var versionNumber = await NextVersionNumberAsync(connection, transaction, draft.ItemId, cancellationToken).ConfigureAwait(false);
            var insertSql = $"""
                INSERT INTO {_versionsTable}
                    (version_id, item_id, package_key, workspace_id, owner_id, version_number, content_hash,
                     envelope, validation, dependencies, provenance,
                     source_draft_id, base_version_id, change_note, created_by, created_at)
                VALUES
                    (@version_id, @item_id, @package_key, @workspace_id, @owner_id, @version_number, @content_hash,
                     @envelope, @validation, @dependencies, @provenance,
                     @source_draft_id, @base_version_id, @change_note, @created_by, @created_at)
                """;
            await using (var command = new NpgsqlCommand(insertSql, connection, transaction))
            {
                command.Parameters.AddWithValue("@version_id", versionId);
                command.Parameters.AddWithValue("@item_id", draft.ItemId);
                command.Parameters.AddWithValue("@package_key", draft.PackageKey);
                command.Parameters.AddWithValue("@workspace_id", (object?)draft.WorkspaceId ?? DBNull.Value);
                command.Parameters.AddWithValue("@owner_id", (object?)draft.OwnerId ?? DBNull.Value);
                command.Parameters.AddWithValue("@version_number", versionNumber);
                command.Parameters.AddWithValue("@content_hash", contentHash);
                command.Parameters.Add(new NpgsqlParameter("@envelope", NpgsqlDbType.Jsonb) { Value = envelopeJson });
                command.Parameters.Add(new NpgsqlParameter("@validation", NpgsqlDbType.Jsonb) { Value = validationJson });
                command.Parameters.Add(new NpgsqlParameter("@dependencies", NpgsqlDbType.Jsonb) { Value = dependenciesJson });
                command.Parameters.Add(new NpgsqlParameter("@provenance", NpgsqlDbType.Jsonb) { Value = provenanceJson });
                command.Parameters.AddWithValue("@source_draft_id", draft.DraftId);
                command.Parameters.AddWithValue("@base_version_id", (object?)draft.BaseVersionId ?? DBNull.Value);
                command.Parameters.AddWithValue("@change_note", (object?)changeNote ?? DBNull.Value);
                command.Parameters.AddWithValue("@created_by", (object?)actorId ?? DBNull.Value);
                command.Parameters.AddWithValue("@created_at", createdAt);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await InsertDependencySidecarsAsync(connection, transaction, draft.ItemId, versionId, draft.Envelope.Dependencies, cancellationToken).ConfigureAwait(false);
            await UpdatePointersAsync(
                connection,
                transaction,
                draft.ItemId,
                currentVersionId: versionId,
                publishedVersionId: null,
                actorId,
                createdAt,
                updateCurrent: true,
                updatePublished: false,
                cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            committed = true;
            return (await GetVersionAsync(draft.ItemId, versionId, cancellationToken).ConfigureAwait(false))!;
        }
        catch
        {
            if (!committed)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
            throw;
        }
    }

    public async Task<IReadOnlyList<StudioContentVersion>> ListVersionsAsync(
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        var sql = VersionSelectSql(_versionsTable) + " WHERE item_id = @item_id ORDER BY version_number";
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, lease.Connection);
        command.Parameters.AddWithValue("@item_id", itemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var versions = new List<StudioContentVersion>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            versions.Add(ReadVersion(reader));
        }

        return versions;
    }

    public async Task<StudioContentVersion?> GetVersionAsync(
        Guid itemId,
        Guid versionId,
        CancellationToken cancellationToken = default)
    {
        var sql = VersionSelectSql(_versionsTable) + " WHERE item_id = @item_id AND version_id = @version_id";
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, lease.Connection);
        command.Parameters.AddWithValue("@item_id", itemId);
        command.Parameters.AddWithValue("@version_id", versionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadVersion(reader)
            : null;
    }

    public async Task<StudioContentItemPointers?> GetPointersAsync(
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT item_id, current_version_id, published_version_id
            FROM {_itemsTable}
            WHERE item_id = @item_id
            """;
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, lease.Connection);
        command.Parameters.AddWithValue("@item_id", itemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadPointers(reader)
            : null;
    }

    public async Task<StudioPublicationRequest> CreatePublicationRequestAsync(
        StudioPublicationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var intentJson = request.Intent is null
            ? null
            : JsonSerializer.Serialize(request.Intent, StudioJsonContext.Default.StudioPublicationIntent);
        var validationJson = SerializeValidation(request.Validation);
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        var connection = lease.Connection;
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        var committed = false;
        try
        {
            var insertSql = $"""
                INSERT INTO {_publicationRequestsTable}
                    (request_id, item_id, version_id, intent, status, validation,
                     warning_acknowledgement, requested_by, created_at)
                VALUES
                    (@request_id, @item_id, @version_id, @intent, @status, @validation,
                     @warning_acknowledgement, @requested_by, @created_at)
                """;
            await using (var command = new NpgsqlCommand(insertSql, connection, transaction))
            {
                command.Parameters.AddWithValue("@request_id", request.RequestId);
                command.Parameters.AddWithValue("@item_id", request.ItemId);
                command.Parameters.AddWithValue("@version_id", request.VersionId);
                command.Parameters.Add(new NpgsqlParameter("@intent", NpgsqlDbType.Jsonb) { Value = (object?)intentJson ?? DBNull.Value });
                command.Parameters.AddWithValue("@status", ToDbPublicationStatus(request.Status));
                command.Parameters.Add(new NpgsqlParameter("@validation", NpgsqlDbType.Jsonb) { Value = validationJson });
                command.Parameters.AddWithValue("@warning_acknowledgement", (object?)request.WarningAcknowledgement ?? DBNull.Value);
                command.Parameters.AddWithValue("@requested_by", (object?)request.RequestedBy ?? DBNull.Value);
                command.Parameters.AddWithValue("@created_at", request.CreatedAt);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (request.Status != StudioPublicationRequestStatus.Rejected)
            {
                await UpdatePointersAsync(
                    connection,
                    transaction,
                    request.ItemId,
                    currentVersionId: null,
                    publishedVersionId: request.VersionId,
                    request.RequestedBy,
                    request.CreatedAt,
                    updateCurrent: false,
                    updatePublished: true,
                    cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            committed = true;
            return request;
        }
        catch
        {
            if (!committed)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
            throw;
        }
    }

    public async Task<StudioRollbackRequest> RollbackAsync(
        Guid itemId,
        Guid targetVersionId,
        StudioRollbackPointer target,
        string? actorId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        var connection = lease.Connection;
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        var committed = false;
        try
        {
            if (!await VersionExistsAsync(connection, transaction, itemId, targetVersionId, cancellationToken).ConfigureAwait(false))
            {
                throw new KeyNotFoundException("Studio content version was not found.");
            }

            var updateCurrent = target is StudioRollbackPointer.Current or StudioRollbackPointer.Both;
            var updatePublished = target is StudioRollbackPointer.Published or StudioRollbackPointer.Both;
            await UpdatePointersAsync(
                connection,
                transaction,
                itemId,
                updateCurrent ? targetVersionId : null,
                updatePublished ? targetVersionId : null,
                actorId,
                now,
                updateCurrent,
                updatePublished,
                cancellationToken).ConfigureAwait(false);

            var pointers = await GetPointersAsync(connection, transaction, itemId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("Studio content item was not found.");
            var request = new StudioRollbackRequest
            {
                RequestId = Guid.NewGuid(),
                ItemId = itemId,
                TargetVersionId = targetVersionId,
                Target = target,
                Pointers = pointers,
                RequestedBy = actorId,
                Reason = reason,
                CreatedAt = now,
            };

            var insertSql = $"""
                INSERT INTO {_rollbackRequestsTable}
                    (request_id, item_id, target_version_id, pointer,
                     current_version_id, published_version_id, requested_by, reason, created_at)
                VALUES
                    (@request_id, @item_id, @target_version_id, @pointer,
                     @current_version_id, @published_version_id, @requested_by, @reason, @created_at)
                """;
            await using (var command = new NpgsqlCommand(insertSql, connection, transaction))
            {
                command.Parameters.AddWithValue("@request_id", request.RequestId);
                command.Parameters.AddWithValue("@item_id", itemId);
                command.Parameters.AddWithValue("@target_version_id", targetVersionId);
                command.Parameters.AddWithValue("@pointer", ToDbRollbackPointer(target));
                command.Parameters.AddWithValue("@current_version_id", (object?)pointers.CurrentVersionId ?? DBNull.Value);
                command.Parameters.AddWithValue("@published_version_id", (object?)pointers.PublishedVersionId ?? DBNull.Value);
                command.Parameters.AddWithValue("@requested_by", (object?)actorId ?? DBNull.Value);
                command.Parameters.AddWithValue("@reason", (object?)reason ?? DBNull.Value);
                command.Parameters.AddWithValue("@created_at", now);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            committed = true;
            return request;
        }
        catch
        {
            if (!committed)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
            throw;
        }
    }

    private async Task UpsertItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid itemId,
        string packageKey,
        string? workspaceId,
        StudioPackageFamily family,
        Guid? currentVersionId,
        Guid? publishedVersionId,
        string? createdBy,
        string? updatedBy,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO {_itemsTable}
                (item_id, package_key, workspace_id, family, current_version_id, published_version_id,
                 created_by, updated_by, created_at, updated_at)
            VALUES
                (@item_id, @package_key, @workspace_id, @family, @current_version_id, @published_version_id,
                 @created_by, @updated_by, @created_at, @updated_at)
            ON CONFLICT (item_id) DO UPDATE
            SET package_key = EXCLUDED.package_key,
                workspace_id = EXCLUDED.workspace_id,
                family = EXCLUDED.family,
                updated_by = EXCLUDED.updated_by,
                updated_at = EXCLUDED.updated_at
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@item_id", itemId);
        command.Parameters.AddWithValue("@package_key", packageKey);
        command.Parameters.AddWithValue("@workspace_id", (object?)workspaceId ?? DBNull.Value);
        command.Parameters.AddWithValue("@family", ToDbFamily(family));
        command.Parameters.AddWithValue("@current_version_id", (object?)currentVersionId ?? DBNull.Value);
        command.Parameters.AddWithValue("@published_version_id", (object?)publishedVersionId ?? DBNull.Value);
        command.Parameters.AddWithValue("@created_by", (object?)createdBy ?? DBNull.Value);
        command.Parameters.AddWithValue("@updated_by", (object?)updatedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("@created_at", createdAt);
        command.Parameters.AddWithValue("@updated_at", updatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdatePointersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid itemId,
        Guid? currentVersionId,
        Guid? publishedVersionId,
        string? actorId,
        DateTimeOffset updatedAt,
        bool updateCurrent,
        bool updatePublished,
        CancellationToken cancellationToken)
    {
        var currentClause = updateCurrent ? "current_version_id = @current_version_id," : string.Empty;
        var publishedClause = updatePublished ? "published_version_id = @published_version_id," : string.Empty;
        var sql = $"""
            UPDATE {_itemsTable}
            SET {currentClause}
                {publishedClause}
                updated_by = @updated_by,
                updated_at = @updated_at
            WHERE item_id = @item_id
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@item_id", itemId);
        command.Parameters.AddWithValue("@current_version_id", (object?)currentVersionId ?? DBNull.Value);
        command.Parameters.AddWithValue("@published_version_id", (object?)publishedVersionId ?? DBNull.Value);
        command.Parameters.AddWithValue("@updated_by", (object?)actorId ?? DBNull.Value);
        command.Parameters.AddWithValue("@updated_at", updatedAt);
        var rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (rows == 0)
        {
            throw new KeyNotFoundException("Studio content item was not found.");
        }
    }

    private async Task InsertDependencySidecarsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid itemId,
        Guid versionId,
        IReadOnlyList<StudioPackageDependency> dependencies,
        CancellationToken cancellationToken)
    {
        if (dependencies.Count == 0)
        {
            return;
        }

        var sql = $"""
            INSERT INTO {_dependenciesTable}
                (version_id, item_id, dependency_kind, dependency_ref, dependency_version_id, required)
            VALUES
                (@version_id, @item_id, @dependency_kind, @dependency_ref, @dependency_version_id, @required)
            ON CONFLICT DO NOTHING
            """;
        foreach (var dependency in dependencies)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("@version_id", versionId);
            command.Parameters.AddWithValue("@item_id", itemId);
            command.Parameters.AddWithValue("@dependency_kind", dependency.Kind);
            command.Parameters.AddWithValue("@dependency_ref", dependency.Ref);
            command.Parameters.AddWithValue("@dependency_version_id", (object?)dependency.VersionId ?? DBNull.Value);
            command.Parameters.AddWithValue("@required", dependency.Required);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> DraftExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid draftId,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT 1 FROM {_draftsTable} WHERE draft_id = @draft_id";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@draft_id", draftId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private async Task<bool> VersionExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid itemId,
        Guid versionId,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT 1 FROM {_versionsTable} WHERE item_id = @item_id AND version_id = @version_id";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@item_id", itemId);
        command.Parameters.AddWithValue("@version_id", versionId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private async Task<int> NextVersionNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT COALESCE(MAX(version_number), 0) + 1 FROM {_versionsTable} WHERE item_id = @item_id";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@item_id", itemId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private async Task<StudioContentItemPointers?> GetPointersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT item_id, current_version_id, published_version_id
            FROM {_itemsTable}
            WHERE item_id = @item_id
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@item_id", itemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadPointers(reader)
            : null;
    }

    private static StudioPackageDraft ReadDraft(NpgsqlDataReader reader)
    {
        var envelope = JsonSerializer.Deserialize(
                reader.GetString(6),
                StudioJsonContext.Default.StudioPackageEnvelope)
            ?? throw new InvalidDataException("Studio package draft envelope was empty.");
        var validation = JsonSerializer.Deserialize(
                reader.GetString(7),
                StudioJsonContext.Default.StudioValidationSummary)
            ?? StudioValidationSummary.NotValidated;

        return new StudioPackageDraft
        {
            DraftId = reader.GetGuid(0),
            ItemId = reader.GetGuid(1),
            PackageKey = reader.GetString(2),
            WorkspaceId = reader.IsDBNull(3) ? null : reader.GetString(3),
            OwnerId = reader.IsDBNull(4) ? null : reader.GetString(4),
            Family = FromDbFamily(reader.GetString(5)),
            Envelope = envelope,
            Validation = validation,
            BaseVersionId = reader.IsDBNull(8) ? null : reader.GetGuid(8),
            Generation = reader.GetInt64(9),
            CreatedBy = reader.IsDBNull(10) ? null : reader.GetString(10),
            UpdatedBy = reader.IsDBNull(11) ? null : reader.GetString(11),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(12),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(13),
        };
    }

    private static StudioContentVersion ReadVersion(NpgsqlDataReader reader)
    {
        var envelope = JsonSerializer.Deserialize(
                reader.GetString(7),
                StudioJsonContext.Default.StudioPackageEnvelope)
            ?? throw new InvalidDataException("Studio content version envelope was empty.");
        var validation = JsonSerializer.Deserialize(
                reader.GetString(8),
                StudioJsonContext.Default.StudioValidationSummary)
            ?? StudioValidationSummary.NotValidated;
        var dependencies = JsonSerializer.Deserialize(
                reader.GetString(9),
                StudioJsonContext.Default.StudioPackageDependencyArray)
            ?? Array.Empty<StudioPackageDependency>();
        var provenance = JsonSerializer.Deserialize(
                reader.GetString(10),
                StudioJsonContext.Default.StudioProvenanceRefArray)
            ?? Array.Empty<StudioProvenanceRef>();

        return new StudioContentVersion
        {
            VersionId = reader.GetGuid(0),
            ItemId = reader.GetGuid(1),
            PackageKey = reader.GetString(2),
            WorkspaceId = reader.IsDBNull(3) ? null : reader.GetString(3),
            OwnerId = reader.IsDBNull(4) ? null : reader.GetString(4),
            VersionNumber = reader.GetInt32(5),
            ContentHash = reader.GetString(6),
            Envelope = envelope,
            Validation = validation,
            Dependencies = dependencies,
            Provenance = provenance,
            SourceDraftId = reader.IsDBNull(11) ? null : reader.GetGuid(11),
            BaseVersionId = reader.IsDBNull(12) ? null : reader.GetGuid(12),
            ChangeNote = reader.IsDBNull(13) ? null : reader.GetString(13),
            CreatedBy = reader.IsDBNull(14) ? null : reader.GetString(14),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(15),
        };
    }

    private static StudioContentItemPointers ReadPointers(NpgsqlDataReader reader)
        => new()
        {
            ItemId = reader.GetGuid(0),
            CurrentVersionId = reader.IsDBNull(1) ? null : reader.GetGuid(1),
            PublishedVersionId = reader.IsDBNull(2) ? null : reader.GetGuid(2),
        };

    private static string VersionSelectSql(string versionsTable) => $"""
        SELECT version_id, item_id, package_key, workspace_id, owner_id, version_number, content_hash,
               envelope, validation, dependencies, provenance,
               source_draft_id, base_version_id, change_note, created_by, created_at
        FROM {versionsTable}
        """;

    private static string SerializeEnvelope(StudioPackageEnvelope envelope)
        => JsonSerializer.Serialize(envelope, StudioJsonContext.Default.StudioPackageEnvelope);

    private static string SerializeValidation(StudioValidationSummary validation)
        => JsonSerializer.Serialize(validation, StudioJsonContext.Default.StudioValidationSummary);

    private static string ToDbFamily(StudioPackageFamily family) => family switch
    {
        StudioPackageFamily.Query => "query",
        StudioPackageFamily.Analysis => "analysis",
        StudioPackageFamily.Map => "map",
        StudioPackageFamily.Dashboard => "dashboard",
        StudioPackageFamily.Report => "report",
        StudioPackageFamily.Form => "form",
        StudioPackageFamily.App => "app",
        StudioPackageFamily.Workflow => "workflow",
        StudioPackageFamily.Geoprocessing => "gp",
        StudioPackageFamily.Etl => "etl",
        _ => throw new ArgumentException("Unsupported Studio package family.", nameof(family)),
    };

    private static StudioPackageFamily FromDbFamily(string family) => family switch
    {
        "query" => StudioPackageFamily.Query,
        "analysis" => StudioPackageFamily.Analysis,
        "map" => StudioPackageFamily.Map,
        "dashboard" => StudioPackageFamily.Dashboard,
        "report" => StudioPackageFamily.Report,
        "form" => StudioPackageFamily.Form,
        "app" => StudioPackageFamily.App,
        "workflow" => StudioPackageFamily.Workflow,
        "gp" => StudioPackageFamily.Geoprocessing,
        "etl" => StudioPackageFamily.Etl,
        _ => throw new InvalidDataException("Unsupported Studio package family in storage."),
    };

    private static string ToDbPublicationStatus(StudioPublicationRequestStatus status) => status switch
    {
        StudioPublicationRequestStatus.Accepted => "accepted",
        StudioPublicationRequestStatus.Pending => "pending",
        StudioPublicationRequestStatus.Rejected => "rejected",
        _ => throw new ArgumentException("Unsupported Studio publication status.", nameof(status)),
    };

    private static string ToDbRollbackPointer(StudioRollbackPointer target) => target switch
    {
        StudioRollbackPointer.Current => "current",
        StudioRollbackPointer.Published => "published",
        StudioRollbackPointer.Both => "both",
        _ => throw new ArgumentException("Unsupported Studio rollback pointer.", nameof(target)),
    };
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Configuration;
using Honua.Core.Exceptions;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Attachments.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Db.Postgres.Features.Attachments;

/// <summary>
/// PostgreSQL implementation of attachment storage and file management
/// </summary>
/// <remarks>
/// Marked as internal to prevent exposure of database-specific implementations
/// outside the Infrastructure layer (Clean Architecture principle).
/// </remarks>
internal sealed partial class PostgresAttachmentStore : IAttachmentStore, IImportedAttachmentStore, IFeatureAttachmentCleanup
{
    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly ICloudFileStorage? _fileStorage;
    private readonly ILogger<PostgresAttachmentStore> _logger;
    private readonly IAttachmentOrphanLedger? _orphanLedger;
    private readonly AttachmentLimits? _attachmentLimits;
    private readonly string _configuredSchema;
    private readonly string _canonicalSchema;
    private readonly PostgresAttachmentMigrationContract? _migrationContract;

    public PostgresAttachmentStore(
        IAdoNetDatabaseConnectionProvider connectionProvider,
        ICloudFileStorage? fileStorage,
        ILogger<PostgresAttachmentStore> logger,
        string? schemaName = null,
        IAttachmentOrphanLedger? orphanLedger = null,
        AttachmentLimits? attachmentLimits = null,
        PostgresAttachmentMigrationContract? migrationContract = null,
        string? canonicalSchemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        // Null is a database-only host. Feature-store resolution includes this store for
        // row cleanup (#6137) and must not require object storage that the host never registered.
        _fileStorage = fileStorage;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _orphanLedger = orphanLedger;
        _attachmentLimits = attachmentLimits;
        _ = Infrastructure.SchemaSearchPath.QualifyTable("attachments", schemaName);
        _configuredSchema = string.IsNullOrWhiteSpace(schemaName)
            ? PostgresSchemaConfiguration.DefaultMetadataSchema : schemaName;
        _canonicalSchema = string.IsNullOrWhiteSpace(canonicalSchemaName) ? _configuredSchema : canonicalSchemaName;
        _ = SchemaSearchPath.ValidateAndQuote(_canonicalSchema);
        _migrationContract = migrationContract;
    }

    /// <summary>
    /// Returns the configured object store. Blob reads and writes call this; a missing
    /// store becomes an orphan record when the metadata change has already committed.
    /// </summary>
    private ICloudFileStorage RequireFileStorage()
        => _fileStorage ?? throw new InvalidOperationException(
            "Object storage is not configured. Register ICloudFileStorage before using attachment blobs.");

    private async Task<(string Attachments, string Cleanup)> ResolveTablesAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, bool requireImportCleanup,
        CancellationToken cancellationToken)
    {
        var contract = _migrationContract ?? throw new InvalidOperationException("Attachment migration identities were not supplied by the host.");
        var physical = await PostgresAttachmentSchema.ResolveAsync(
            connection, _canonicalSchema, contract, transaction, requireImportCleanup: requireImportCleanup,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!string.Equals(_configuredSchema, _canonicalSchema, StringComparison.Ordinal))
        {
            var overridden = await PostgresAttachmentSchema.ResolveAsync(
                connection, _configuredSchema, contract, transaction, requireImportCleanup: requireImportCleanup,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!string.Equals(physical, overridden, StringComparison.Ordinal))
            {
                throw new AttachmentSchemaBindingException("The explicit attachment schema resolves to a different canonical physical authority.");
            }
        }
        return (Infrastructure.SchemaSearchPath.QualifyTable("attachments", physical),
            Infrastructure.SchemaSearchPath.QualifyTable("import_attachment_cleanup", physical));
    }

    /// <summary>
    /// Records a storage object that outlived its metadata row, so the divergence is
    /// enumerable and alertable rather than only logged. Never throws: an orphan record
    /// must not mask the original failure nor fail an otherwise successful delete.
    /// </summary>
    /// <remarks>
    /// Deliberately not cancellable. Every call site is a failure or cancellation path, so
    /// the caller's token is frequently already canceled; honouring it would discard the
    /// only evidence that the two stores diverged.
    /// </remarks>
    private async Task RecordOrphanAsync(
        string storagePath,
        int layerId,
        long featureId,
        AttachmentOrphanKind kind,
        string? reason)
    {
        if (_orphanLedger == null)
        {
            return;
        }

        try
        {
            await _orphanLedger.RecordAsync(
                new AttachmentOrphan(storagePath, layerId, featureId, kind, DateTimeOffset.UtcNow, reason),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AttachmentLog.AttachmentCleanupFailed(_logger, ex, storagePath);
        }
    }

    public async Task<Attachment?> GetAsync(int layerId, long featureId, long attachmentId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        var tables = await ResolveTablesAsync(connection, null, false, cancellationToken).ConfigureAwait(false);
        var sql = $@"
            SELECT id, feature_id, layer_id, filename, content_type, size, created_at, storage_path, keywords
            FROM {tables.Attachments}
            WHERE layer_id = $1 AND feature_id = $2 AND id = $3";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(layerId);
        command.Parameters.AddWithValue(featureId);
        command.Parameters.AddWithValue(attachmentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadAttachment(reader);
    }

    public async Task<Attachment[]> ListAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        var tables = await ResolveTablesAsync(connection, null, false, cancellationToken).ConfigureAwait(false);
        var sql = $@"
            SELECT id, feature_id, layer_id, filename, content_type, size, created_at, storage_path, keywords
            FROM {tables.Attachments}
            WHERE layer_id = $1 AND feature_id = $2
            ORDER BY created_at DESC";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(layerId);
        command.Parameters.AddWithValue(featureId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var attachments = new List<Attachment>();
        while (await reader.ReadAsync(cancellationToken))
        {
            attachments.Add(ReadAttachment(reader));
        }

        return attachments.ToArray();
    }

    public async Task<Attachment> CreateAsync(int layerId, long featureId, Attachment attachment, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await InsertAttachmentAsync(connection, transaction: null, layerId, featureId, attachment, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Attachment> UpdateAsync(int layerId, long featureId, Attachment attachment, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        var tables = await ResolveTablesAsync(connection, null, false, cancellationToken).ConfigureAwait(false);
        var sql = $@"
            UPDATE {tables.Attachments}
            SET filename = $4, content_type = $5, size = $6, storage_path = $7, keywords = $8
            WHERE layer_id = $1 AND feature_id = $2 AND id = $3
            RETURNING id, feature_id, layer_id, filename, content_type, size, created_at, storage_path, keywords";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(layerId);
        command.Parameters.AddWithValue(featureId);
        command.Parameters.AddWithValue(attachment.Id);
        command.Parameters.AddWithValue(attachment.Filename);
        command.Parameters.AddWithValue(attachment.ContentType);
        command.Parameters.AddWithValue(attachment.Size);
        command.Parameters.AddWithValue(attachment.StoragePath);
        command.Parameters.AddWithValue(attachment.Keywords ?? (object)DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new ResourceNotFoundException($"Attachment {attachment.Id} not found for update");
        }

        return ReadAttachment(reader);
    }

    public async Task<Attachment> UpdateKeywordsAsync(
        int layerId,
        long featureId,
        long attachmentId,
        string? keywords,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        var tables = await ResolveTablesAsync(connection, null, false, cancellationToken).ConfigureAwait(false);
        var sql = $@"
            UPDATE {tables.Attachments}
            SET keywords = $4
            WHERE layer_id = $1 AND feature_id = $2 AND id = $3
            RETURNING id, feature_id, layer_id, filename, content_type, size, created_at, storage_path, keywords";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(layerId);
        command.Parameters.AddWithValue(featureId);
        command.Parameters.AddWithValue(attachmentId);
        command.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, keywords ?? (object)DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new ResourceNotFoundException($"Attachment {attachmentId} not found for update");
        }

        return ReadAttachment(reader);
    }

    public async Task<Attachment> ReplaceAsync(
        int layerId,
        long featureId,
        long attachmentId,
        string filename,
        string contentType,
        Stream content,
        string? keywords = null,
        CancellationToken cancellationToken = default)
    {
        var existingAttachment = await GetAsync(layerId, featureId, attachmentId, cancellationToken).ConfigureAwait(false);
        if (!existingAttachment.HasValue)
        {
            throw new ResourceNotFoundException($"Attachment {attachmentId} not found for update");
        }

        var existing = existingAttachment.Value;
        var sanitizedFilename = SanitizeFileName(filename);
        var folder = BuildAttachmentFolder(layerId, featureId);
        var sizeBytes = content.CanSeek ? content.Length : (long?)null;

        var uploadResult = await RequireFileStorage().UploadAsync(new FileUploadRequest
        {
            Content = content,
            FileName = sanitizedFilename,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            Folder = folder
        }, cancellationToken).ConfigureAwait(false);

        if (!uploadResult.Success || uploadResult.File == null)
        {
            var message = uploadResult.ErrorMessage ?? "Attachment upload failed.";
            AttachmentLog.AttachmentUploadFailed(_logger, layerId, featureId, message);
            throw new InvalidOperationException(message);
        }

        var updatedAttachment = Attachment.Create(
            existing.Id,
            existing.FeatureId,
            existing.LayerId,
            sanitizedFilename,
            uploadResult.File.ContentType,
            uploadResult.File.SizeBytes,
            existing.CreatedAt,
            uploadResult.File.FileId,
            keywords);

        try
        {
            var updated = await UpdateAsync(layerId, featureId, updatedAttachment, cancellationToken).ConfigureAwait(false);

            if (!string.Equals(existing.StoragePath, updated.StoragePath, StringComparison.Ordinal))
            {
                try
                {
                    var deleted = await RequireFileStorage().DeleteAsync(existing.StoragePath, cancellationToken).ConfigureAwait(false);
                    if (!deleted)
                    {
                        // ICloudFileStorage.DeleteAsync defines false as "file was not found",
                        // so the superseded object is already gone and the two stores agree.
                        // Nothing to reconcile: log it, but do not report an orphan.
                        AttachmentLog.AttachmentFileMissing(_logger, existing.StoragePath, layerId, featureId);
                    }
                }
                catch (OperationCanceledException ex)
                {
                    // The update is already committed and the row no longer names this object,
                    // so a cancellation here leaks it exactly as a storage failure would.
                    await RecordOrphanAsync(
                        existing.StoragePath,
                        layerId,
                        featureId,
                        AttachmentOrphanKind.UndeletedObject,
                        $"superseded-object delete was canceled after the metadata update committed ({ex.GetType().Name})").ConfigureAwait(false);
                    throw;
                }
                // codeql[cs/catch-of-all-exceptions]: any storage failure becomes an orphan record so the committed metadata update is not rolled back
                catch (Exception ex)
                {
                    AttachmentLog.AttachmentFileDeleteFailed(_logger, ex, existing.StoragePath);
                    await RecordOrphanAsync(
                        existing.StoragePath,
                        layerId,
                        featureId,
                        AttachmentOrphanKind.UndeletedObject,
                        ex.Message).ConfigureAwait(false);
                }
            }

            return updated;
        }
        catch (Exception updateException)
        {
            try
            {
                // A false result means the object was not found, so the upload left nothing
                // behind and the compensation is complete; only a failure leaks an object.
                await RequireFileStorage().DeleteAsync(uploadResult.File.FileId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AttachmentLog.AttachmentCleanupFailed(_logger, ex, uploadResult.File.FileId);
                await RecordOrphanAsync(
                    uploadResult.File.FileId,
                    layerId,
                    featureId,
                    AttachmentOrphanKind.ObjectWithoutMetadata,
                    $"replace metadata update failed ({updateException.GetType().Name}) and compensating delete failed ({ex.Message})").ConfigureAwait(false);
            }

            throw;
        }
    }

    public async Task<bool> DeleteAsync(int layerId, long featureId, long attachmentId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        var tables = await ResolveTablesAsync(connection, null, false, cancellationToken).ConfigureAwait(false);
        var sql = $@"
            DELETE FROM {tables.Attachments}
            WHERE layer_id = $1 AND feature_id = $2 AND id = $3
            RETURNING storage_path";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(layerId);
        command.Parameters.AddWithValue(featureId);
        command.Parameters.AddWithValue(attachmentId);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is not string storagePath)
        {
            return false;
        }

        try
        {
            var deleted = await RequireFileStorage().DeleteAsync(storagePath, cancellationToken);
            if (!deleted)
            {
                // ICloudFileStorage.DeleteAsync defines false as "file was not found". The row
                // is gone and so is the object, so the two stores agree and there is nothing to
                // reconcile — recording an orphan here would be a false positive.
                AttachmentLog.AttachmentFileMissing(_logger, storagePath, layerId, featureId);
            }
        }
        catch (OperationCanceledException ex)
        {
            // The row was committed away before storage deletion began, so a cancellation
            // mid-delete leaves an object nothing references. Record it before rethrowing:
            // this is exactly the divergence the ledger exists to surface.
            await RecordOrphanAsync(
                storagePath,
                layerId,
                featureId,
                AttachmentOrphanKind.UndeletedObject,
                $"storage delete was canceled after the metadata row was committed away ({ex.GetType().Name})").ConfigureAwait(false);
            throw;
        }
        // codeql[cs/catch-of-all-exceptions]: any storage failure becomes an orphan record so the committed metadata delete is not rolled back
        catch (Exception ex)
        {
            AttachmentLog.AttachmentFileDeleteFailed(_logger, ex, storagePath);
            await RecordOrphanAsync(
                storagePath,
                layerId,
                featureId,
                AttachmentOrphanKind.UndeletedObject,
                ex.Message).ConfigureAwait(false);
        }

        return true;
    }

    public async Task<Attachment> UploadAsync(int layerId, long featureId, string filename, string contentType, Stream content, string? keywords = null, CancellationToken cancellationToken = default)
    {
        // Sanitize filename to prevent path traversal attacks
        var sanitizedFilename = SanitizeFileName(filename);
        var folder = BuildAttachmentFolder(layerId, featureId);
        var sizeBytes = content.CanSeek ? content.Length : (long?)null;

        var uploadResult = await RequireFileStorage().UploadAsync(new FileUploadRequest
        {
            Content = content,
            FileName = sanitizedFilename,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            Folder = folder
        }, cancellationToken);

        if (!uploadResult.Success || uploadResult.File == null)
        {
            var message = uploadResult.ErrorMessage ?? "Attachment upload failed.";
            AttachmentLog.AttachmentUploadFailed(_logger, layerId, featureId, message);
            throw new InvalidOperationException(message);
        }

        // Create attachment record
        var attachment = Attachment.CreateForUpload(
            id: 0, // Will be assigned by database
            featureId: featureId,
            layerId: layerId,
            filename: sanitizedFilename,
            contentType: uploadResult.File.ContentType,
            size: uploadResult.File.SizeBytes,
            storagePath: uploadResult.File.FileId,
            keywords: keywords);

        try
        {
            if (_attachmentLimits is null)
            {
                return await CreateAsync(layerId, featureId, attachment, cancellationToken).ConfigureAwait(false);
            }

            return await InsertWithinQuotaAsync(layerId, featureId, attachment, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception insertException)
        {
            try
            {
                // Best-effort compensating cleanup of the orphaned uploaded file after the metadata
                // insert failed; a cleanup failure must not mask the original exception being rethrown.
                // A false result means the object was not found, so nothing survived the failed
                // insert; only a throwing cleanup leaves an object with no row.
                await RequireFileStorage().DeleteAsync(uploadResult.File.FileId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AttachmentLog.AttachmentCleanupFailed(_logger, ex, uploadResult.File.FileId);

                // Both halves failed: the object is live and no row will ever reference it.
                await RecordOrphanAsync(
                    uploadResult.File.FileId,
                    layerId,
                    featureId,
                    AttachmentOrphanKind.ObjectWithoutMetadata,
                    $"metadata insert failed ({insertException.GetType().Name}) and compensating delete failed ({ex.Message})").ConfigureAwait(false);
            }

            throw;
        }
    }

    public async Task<AttachmentContent?> DownloadAsync(int layerId, long featureId, long attachmentId, CancellationToken cancellationToken = default)
    {
        var attachment = await GetAsync(layerId, featureId, attachmentId, cancellationToken);
        if (attachment == null)
        {
            return null;
        }

        var fileStream = await RequireFileStorage().DownloadAsync(attachment.Value.StoragePath, cancellationToken);
        if (fileStream == null)
        {
            return null;
        }

        return AttachmentContent.Create(attachment.Value, fileStream);
    }

    /// <summary>
    /// Sanitizes a filename to prevent path traversal and other security issues.
    /// </summary>
    private static string SanitizeFileName(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            return "unnamed_file";
        }

        // Remove null bytes and control characters
        var cleanedName = new string(filename.Where(c => c >= 32 && c != 127).ToArray());

        // Get just the filename, not any path components
        var baseName = Path.GetFileName(cleanedName);

        // Remove dangerous characters
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(baseName
            .Where(c => !invalidChars.Contains(c) && c != '<' && c != '>' && c != '"' && c != '\'')
            .ToArray());

        // Collapse path traversal patterns
        while (sanitized.Contains("..", StringComparison.Ordinal))
        {
            sanitized = sanitized.Replace("..", ".", StringComparison.Ordinal);
        }

        // Trim leading/trailing dots
        sanitized = sanitized.Trim('.');

        // Ensure filename is not too long (preserve extension)
        if (sanitized.Length > 200)
        {
            var extension = Path.GetExtension(sanitized);
            var nameWithoutExtension = Path.GetFileNameWithoutExtension(sanitized);
            sanitized = nameWithoutExtension[..Math.Min(200 - extension.Length, nameWithoutExtension.Length)] + extension;
        }

        return string.IsNullOrWhiteSpace(sanitized) ? "sanitized_file" : sanitized;
    }

    private static string BuildAttachmentFolder(int layerId, long featureId)
    {
        return string.Join(
            '/',
            "attachments",
            layerId.ToString(CultureInfo.InvariantCulture),
            featureId.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Holds a transaction advisory lock for this feature, recounts its attachments, and
    /// inserts only when the configured count and size limits still hold. A refusal throws
    /// <see cref="ValidationException"/> so the caller can delete the blob it already stored.
    /// </summary>
    private async Task<Attachment> InsertWithinQuotaAsync(
        int layerId,
        long featureId,
        Attachment attachment,
        CancellationToken cancellationToken)
    {
        var limits = _attachmentLimits ?? throw new InvalidOperationException("Attachment limits are not configured.");
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var tables = await ResolveTablesAsync(connection, transaction, false, cancellationToken).ConfigureAwait(false);

        // hashtextextended keeps the lock in the bigint form. Feature ids do not fit the
        // two-int overload, and the prefix does not share the feature-edit lock key.
        await using (var lease = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended('attachment:' || $1::text || ':' || $2::text, 0))",
            connection,
            transaction))
        {
            lease.Parameters.AddWithValue(layerId);
            lease.Parameters.AddWithValue(featureId);
            await lease.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        long count;
        long totalSize;
        await using (var countCommand = new NpgsqlCommand($"""
            SELECT COUNT(*)::bigint, COALESCE(SUM(size), 0)::bigint
            FROM {tables.Attachments}
            WHERE layer_id = $1 AND feature_id = $2
            """, connection, transaction))
        {
            countCommand.Parameters.AddWithValue(layerId);
            countCommand.Parameters.AddWithValue(featureId);
            await using var reader = await countCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Failed to count attachments");
            }

            count = reader.GetInt64(0);
            totalSize = reader.GetInt64(1);
        }

        if (count >= limits.MaxAttachmentsPerFeature)
        {
            throw new ValidationException(
                $"Feature already has the maximum number of attachments ({limits.MaxAttachmentsPerFeature})");
        }

        if (attachment.Size > limits.MaxTotalAttachmentSize ||
            totalSize > limits.MaxTotalAttachmentSize - attachment.Size)
        {
            throw new ValidationException(
                $"Total attachment size would exceed maximum allowed ({limits.MaxTotalAttachmentSize:N0} bytes)");
        }

        var created = await InsertAttachmentAsync(connection, transaction, layerId, featureId, attachment, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return created;
    }

    private async Task<Attachment> InsertAttachmentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        int layerId,
        long featureId,
        Attachment attachment,
        CancellationToken cancellationToken)
    {
        var tables = await ResolveTablesAsync(connection, transaction, true, cancellationToken).ConfigureAwait(false);
        var sql = $@"
            INSERT INTO {tables.Attachments} (feature_id, layer_id, filename, content_type, size, created_at, storage_path, keywords, attachment_origin)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, 'honua')
            RETURNING id, feature_id, layer_id, filename, content_type, size, created_at, storage_path, keywords";

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(featureId);
        command.Parameters.AddWithValue(layerId);
        command.Parameters.AddWithValue(attachment.Filename);
        command.Parameters.AddWithValue(attachment.ContentType);
        command.Parameters.AddWithValue(attachment.Size);
        command.Parameters.AddWithValue(attachment.CreatedAt);
        command.Parameters.AddWithValue(attachment.StoragePath);
        command.Parameters.AddWithValue(attachment.Keywords ?? (object)DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Failed to create attachment");
        }

        return ReadAttachment(reader);
    }

    private static Attachment ReadAttachment(NpgsqlDataReader reader)
    {
        var id = reader.GetInt64(0); // id
        var featureId = reader.GetInt64(1); // feature_id
        var layerId = reader.GetInt32(2); // layer_id
        var filename = reader.GetString(3); // filename
        var contentType = reader.GetString(4); // content_type
        var size = reader.GetInt64(5); // size
        var createdAt = reader.GetDateTime(6); // created_at
        var storagePath = reader.GetString(7); // storage_path
        var keywords = reader.IsDBNull(8) ? null : reader.GetString(8); // keywords

        return Attachment.Create(id, featureId, layerId, filename, contentType, size, createdAt, storagePath, keywords);
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Exceptions;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Attachments.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Postgres.Features.Attachments;

/// <summary>
/// PostgreSQL implementation of attachment storage and file management
/// </summary>
/// <remarks>
/// Marked as internal to prevent exposure of database-specific implementations
/// outside the Infrastructure layer (Clean Architecture principle).
/// </remarks>
internal sealed class PostgresAttachmentStore : IAttachmentStore
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ICloudFileStorage _fileStorage;
    private readonly ILogger<PostgresAttachmentStore> _logger;
    private readonly string _tableName;

    public PostgresAttachmentStore(
        IDatabaseConnectionProvider connectionProvider,
        ICloudFileStorage fileStorage,
        ILogger<PostgresAttachmentStore> logger,
        string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _fileStorage = fileStorage ?? throw new ArgumentNullException(nameof(fileStorage));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tableName = Infrastructure.SchemaSearchPath.QualifyTable("attachments", schemaName);
    }

    public async Task<Attachment?> GetAsync(int layerId, long featureId, long attachmentId, CancellationToken cancellationToken = default)
    {
        var sql = $@"
            SELECT id, feature_id, layer_id, filename, content_type, size, created_at, storage_path, keywords
            FROM {_tableName}
            WHERE layer_id = $1 AND feature_id = $2 AND id = $3";

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
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
        var sql = $@"
            SELECT id, feature_id, layer_id, filename, content_type, size, created_at, storage_path, keywords
            FROM {_tableName}
            WHERE layer_id = $1 AND feature_id = $2
            ORDER BY created_at DESC";

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
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
        var sql = $@"
            INSERT INTO {_tableName} (feature_id, layer_id, filename, content_type, size, created_at, storage_path, keywords)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            RETURNING id, feature_id, layer_id, filename, content_type, size, created_at, storage_path, keywords";

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(featureId);
        command.Parameters.AddWithValue(layerId);
        command.Parameters.AddWithValue(attachment.Filename);
        command.Parameters.AddWithValue(attachment.ContentType);
        command.Parameters.AddWithValue(attachment.Size);
        command.Parameters.AddWithValue(attachment.CreatedAt);
        command.Parameters.AddWithValue(attachment.StoragePath);
        command.Parameters.AddWithValue(attachment.Keywords ?? (object)DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Failed to create attachment");
        }

        return ReadAttachment(reader);
    }

    public async Task<Attachment> UpdateAsync(int layerId, long featureId, Attachment attachment, CancellationToken cancellationToken = default)
    {
        var sql = $@"
            UPDATE {_tableName}
            SET filename = $4, content_type = $5, keywords = $6
            WHERE layer_id = $1 AND feature_id = $2 AND id = $3
            RETURNING id, feature_id, layer_id, filename, content_type, size, created_at, storage_path, keywords";

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(layerId);
        command.Parameters.AddWithValue(featureId);
        command.Parameters.AddWithValue(attachment.Id);
        command.Parameters.AddWithValue(attachment.Filename);
        command.Parameters.AddWithValue(attachment.ContentType);
        command.Parameters.AddWithValue(attachment.Keywords ?? (object)DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new ResourceNotFoundException($"Attachment {attachment.Id} not found for update");
        }

        return ReadAttachment(reader);
    }

    public async Task<bool> DeleteAsync(int layerId, long featureId, long attachmentId, CancellationToken cancellationToken = default)
    {
        var sql = $@"
            DELETE FROM {_tableName}
            WHERE layer_id = $1 AND feature_id = $2 AND id = $3
            RETURNING storage_path";

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
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
            var deleted = await _fileStorage.DeleteAsync(storagePath, cancellationToken);
            if (!deleted)
            {
                AttachmentLog.AttachmentFileMissing(_logger, storagePath, layerId, featureId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AttachmentLog.AttachmentFileDeleteFailed(_logger, ex, storagePath);
        }

        return true;
    }

    public async Task<Attachment> UploadAsync(int layerId, long featureId, string filename, string contentType, Stream content, string? keywords = null, CancellationToken cancellationToken = default)
    {
        // Sanitize filename to prevent path traversal attacks
        var sanitizedFilename = SanitizeFileName(filename);
        var folder = BuildAttachmentFolder(layerId, featureId);
        var sizeBytes = content.CanSeek ? content.Length : (long?)null;

        var uploadResult = await _fileStorage.UploadAsync(new FileUploadRequest
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
            return await CreateAsync(layerId, featureId, attachment, cancellationToken);
        }
        catch
        {
            try
            {
                await _fileStorage.DeleteAsync(uploadResult.File.FileId, cancellationToken);
            }
            catch (Exception ex)
            {
                AttachmentLog.AttachmentCleanupFailed(_logger, ex, uploadResult.File.FileId);
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

        var fileStream = await _fileStorage.DownloadAsync(attachment.Value.StoragePath, cancellationToken);
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

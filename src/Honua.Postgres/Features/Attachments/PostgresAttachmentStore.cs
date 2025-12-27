// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Attachments.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
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
    private readonly string _tableName;
    private readonly string _storageBasePath;

    public PostgresAttachmentStore(IDatabaseConnectionProvider connectionProvider, string? schemaName = null, string? storageBasePath = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _tableName = string.IsNullOrEmpty(schemaName) ? "attachments" : $"{schemaName}.attachments";
        _storageBasePath = storageBasePath ?? Path.Combine(Directory.GetCurrentDirectory(), "attachments");

        // Ensure storage directory exists
        Directory.CreateDirectory(_storageBasePath);
    }

    public async Task<Attachment?> GetAsync(int layerId, long featureId, long attachmentId, CancellationToken cancellationToken = default)
    {
        var sql = $@"
            SELECT id, feature_id, layer_id, filename, content_type, size, created_at, storage_path, keywords
            FROM {_tableName}
            WHERE layer_id = $1 AND feature_id = $2 AND id = $3";

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(layerId);
        command.Parameters.AddWithValue(featureId);
        command.Parameters.AddWithValue(attachmentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return await ReadAttachmentAsync(reader, cancellationToken);
    }

    public async Task<Attachment[]> ListAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        var sql = $@"
            SELECT id, feature_id, layer_id, filename, content_type, size, created_at, storage_path, keywords
            FROM {_tableName}
            WHERE layer_id = $1 AND feature_id = $2
            ORDER BY created_at DESC";

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(layerId);
        command.Parameters.AddWithValue(featureId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var attachments = new List<Attachment>();
        while (await reader.ReadAsync(cancellationToken))
        {
            attachments.Add(await ReadAttachmentAsync(reader, cancellationToken));
        }

        return attachments.ToArray();
    }

    public async Task<Attachment> CreateAsync(int layerId, long featureId, Attachment attachment, CancellationToken cancellationToken = default)
    {
        var sql = $@"
            INSERT INTO {_tableName} (feature_id, layer_id, filename, content_type, size, created_at, storage_path, keywords)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            RETURNING id, feature_id, layer_id, filename, content_type, size, created_at, storage_path, keywords";

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
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

        return await ReadAttachmentAsync(reader, cancellationToken);
    }

    public async Task<Attachment> UpdateAsync(int layerId, long featureId, Attachment attachment, CancellationToken cancellationToken = default)
    {
        var sql = $@"
            UPDATE {_tableName}
            SET filename = $4, content_type = $5, keywords = $6
            WHERE layer_id = $1 AND feature_id = $2 AND id = $3
            RETURNING id, feature_id, layer_id, filename, content_type, size, created_at, storage_path, keywords";

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
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

        return await ReadAttachmentAsync(reader, cancellationToken);
    }

    public async Task<bool> DeleteAsync(int layerId, long featureId, long attachmentId, CancellationToken cancellationToken = default)
    {
        // First get the storage path to delete the physical file
        var attachment = await GetAsync(layerId, featureId, attachmentId, cancellationToken);
        if (attachment == null)
        {
            return false;
        }

        // Delete the database record
        var sql = $@"
            DELETE FROM {_tableName}
            WHERE layer_id = $1 AND feature_id = $2 AND id = $3";

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(layerId);
        command.Parameters.AddWithValue(featureId);
        command.Parameters.AddWithValue(attachmentId);

        var deletedRows = await command.ExecuteNonQueryAsync(cancellationToken);

        // Delete the physical file if database record was deleted
        if (deletedRows > 0)
        {
            var fullPath = Path.Combine(_storageBasePath, attachment.Value.StoragePath);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
            return true;
        }

        return false;
    }

    public async Task<Attachment> UploadAsync(int layerId, long featureId, string filename, string contentType, Stream content, string? keywords = null, CancellationToken cancellationToken = default)
    {
        // Generate storage path
        var fileExtension = Path.GetExtension(filename);
        var storagePath = $"{layerId}/{featureId}/{Guid.NewGuid()}{fileExtension}";
        var fullPath = Path.Combine(_storageBasePath, storagePath);

        // Ensure directory exists
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);

        // Save file to storage
        long fileSize;
        await using (var fileStream = new FileStream(fullPath, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        }))
        {
            await content.CopyToAsync(fileStream, cancellationToken);
            fileSize = fileStream.Length;
        }

        // Create attachment record
        var attachment = Attachment.CreateForUpload(
            id: 0, // Will be assigned by database
            featureId: featureId,
            layerId: layerId,
            filename: filename,
            contentType: contentType,
            size: fileSize,
            storagePath: storagePath,
            keywords: keywords);

        try
        {
            return await CreateAsync(layerId, featureId, attachment, cancellationToken);
        }
        catch
        {
            if (File.Exists(fullPath))
            {
                try
                {
                    File.Delete(fullPath);
                }
                catch
                {
                    // Best effort cleanup: ignore file delete failures to preserve original exception.
                }
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

        var fullPath = Path.Combine(_storageBasePath, attachment.Value.StoragePath);
        if (!File.Exists(fullPath))
        {
            return null;
        }

        var fileStream = new FileStream(fullPath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        return AttachmentContent.Create(attachment.Value, fileStream);
    }

    private static async Task<Attachment> ReadAttachmentAsync(NpgsqlDataReader reader, CancellationToken cancellationToken)
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

        await Task.CompletedTask; // Satisfy async context

        return Attachment.Create(id, featureId, layerId, filename, contentType, size, createdAt, storagePath, keywords);
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Abstract cloud file storage provider supporting AWS S3 and Azure Blob Storage
/// for temporary file storage during geospatial data upload and import processing.
/// </summary>
public interface ICloudFileStorage
{
    /// <summary>
    /// Gets the storage provider type for this implementation
    /// </summary>
    CloudStorageProvider Provider { get; }

    // Single file operations

    /// <summary>
    /// Uploads a file from a stream
    /// </summary>
    /// <param name="request">Upload request with file content and metadata</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Upload result with file information or error details</returns>
    Task<UploadResult> UploadAsync(FileUploadRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a file from a byte array
    /// </summary>
    /// <param name="request">Upload request with file content and metadata</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Upload result with file information or error details</returns>
    Task<UploadResult> UploadAsync(ByteArrayUploadRequest request, CancellationToken cancellationToken = default);

    // Upload progress tracking

    /// <summary>
    /// Gets the current upload progress for a specific upload operation
    /// </summary>
    /// <param name="uploadId">Unique identifier for the upload operation</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Current upload progress, or null if upload not found</returns>
    Task<UploadProgress?> GetUploadProgressAsync(string uploadId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels an active upload operation
    /// </summary>
    /// <param name="uploadId">Unique identifier for the upload operation</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if upload was cancelled, false if upload not found or already completed</returns>
    Task<bool> CancelUploadAsync(string uploadId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all active upload operations
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of active upload operations</returns>
    Task<IReadOnlyList<UploadProgress>> GetActiveUploadsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a file as a stream
    /// </summary>
    /// <param name="fileId">Unique file identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Stream containing file content, or null if file not found</returns>
    Task<Stream?> DownloadAsync(string fileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a file as a byte array
    /// </summary>
    /// <param name="fileId">Unique file identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Byte array containing file content, or null if file not found</returns>
    Task<byte[]?> DownloadBytesAsync(string fileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a file from storage
    /// </summary>
    /// <param name="fileId">Unique file identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if file was deleted, false if file was not found</returns>
    Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken = default);

    // Batch operations

    /// <summary>
    /// Uploads multiple files as a batch (e.g., Shapefile components)
    /// </summary>
    /// <param name="request">Batch upload request with files and metadata</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Batch upload result with success/failure details per file</returns>
    Task<BatchUploadResult> UploadBatchAsync(BatchUploadRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes multiple files by their batch ID
    /// </summary>
    /// <param name="batchId">Batch identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of files deleted</returns>
    Task<int> DeleteBatchAsync(string batchId, CancellationToken cancellationToken = default);

    // Metadata operations

    /// <summary>
    /// Retrieves file metadata without downloading content
    /// </summary>
    /// <param name="fileId">Unique file identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>File metadata, or null if file not found</returns>
    Task<CloudFile?> GetMetadataAsync(string fileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a file exists in storage
    /// </summary>
    /// <param name="fileId">Unique file identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if file exists, false otherwise</returns>
    Task<bool> ExistsAsync(string fileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists files in a folder/prefix
    /// </summary>
    /// <param name="folder">Optional folder path to list (null for root)</param>
    /// <param name="maxResults">Maximum number of results to return</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of files in the specified folder</returns>
    Task<IReadOnlyList<CloudFile>> ListFilesAsync(
        string? folder = null,
        int maxResults = 1000,
        CancellationToken cancellationToken = default);

    // URL operations

    /// <summary>
    /// Generates a secure, time-limited URL for downloading a file
    /// </summary>
    /// <param name="fileId">Unique file identifier</param>
    /// <param name="expiresIn">How long the URL should be valid</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Presigned URL, or null if file not found</returns>
    Task<string?> GetPresignedUrlAsync(
        string fileId,
        TimeSpan? expiresIn = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a secure, time-limited URL for uploading a file directly
    /// </summary>
    /// <param name="fileName">Desired filename</param>
    /// <param name="contentType">Expected content type</param>
    /// <param name="expiresIn">How long the URL should be valid</param>
    /// <param name="folder">Optional folder for the file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Presigned upload URL and the file ID that will be assigned</returns>
    Task<(string Url, string FileId)?> GetPresignedUploadUrlAsync(
        string fileName,
        string contentType,
        TimeSpan? expiresIn = null,
        string? folder = null,
        CancellationToken cancellationToken = default);

    // Cleanup operations

    /// <summary>
    /// Removes expired temporary files
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of files cleaned up</returns>
    Task<int> CleanupExpiredFilesAsync(CancellationToken cancellationToken = default);
}

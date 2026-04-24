// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.Attachments.Domain;

namespace Honua.Core.Features.Attachments.Abstractions;

/// <summary>
/// Abstraction for attachment storage and retrieval operations
/// </summary>
public interface IAttachmentStore
{
    // Query operations

    /// <summary>
    /// Retrieves an attachment by its unique identifier
    /// </summary>
    /// <param name="layerId">Layer identifier containing the feature</param>
    /// <param name="featureId">Feature identifier that owns the attachment</param>
    /// <param name="attachmentId">Unique attachment identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Attachment if found, null otherwise</returns>
    Task<Attachment?> GetAsync(int layerId, long featureId, long attachmentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all attachments for a specific feature
    /// </summary>
    /// <param name="layerId">Layer identifier containing the feature</param>
    /// <param name="featureId">Feature identifier that owns the attachments</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Array of attachments for the feature</returns>
    Task<Attachment[]> ListAsync(int layerId, long featureId, CancellationToken cancellationToken = default);

    // Edit operations

    /// <summary>
    /// Creates a new attachment
    /// </summary>
    /// <param name="layerId">Layer identifier where the attachment should be created</param>
    /// <param name="featureId">Feature identifier that will own the attachment</param>
    /// <param name="attachment">Attachment to create (Id may be ignored and auto-generated)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created attachment with assigned ID</returns>
    Task<Attachment> CreateAsync(int layerId, long featureId, Attachment attachment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing attachment record
    /// </summary>
    /// <param name="layerId">Layer identifier containing the feature</param>
    /// <param name="featureId">Feature identifier that owns the attachment</param>
    /// <param name="attachment">Attachment with updated values (must include valid Id)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated attachment</returns>
    /// <exception cref="ResourceNotFoundException">Thrown if attachment with the specified ID does not exist</exception>
    Task<Attachment> UpdateAsync(int layerId, long featureId, Attachment attachment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the stored file content for an existing attachment while preserving its identifier.
    /// </summary>
    /// <param name="layerId">Layer identifier containing the feature</param>
    /// <param name="featureId">Feature identifier that owns the attachment</param>
    /// <param name="attachmentId">Unique attachment identifier to replace</param>
    /// <param name="filename">Updated filename for the attachment</param>
    /// <param name="contentType">Updated MIME content type</param>
    /// <param name="content">Replacement file content stream</param>
    /// <param name="keywords">Optional updated keywords</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated attachment with the new stored file reference</returns>
    /// <exception cref="ResourceNotFoundException">Thrown if attachment with the specified ID does not exist</exception>
    Task<Attachment> ReplaceAsync(
        int layerId,
        long featureId,
        long attachmentId,
        string filename,
        string contentType,
        Stream content,
        string? keywords = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an attachment by its unique identifier
    /// </summary>
    /// <param name="layerId">Layer identifier containing the feature</param>
    /// <param name="featureId">Feature identifier that owns the attachment</param>
    /// <param name="attachmentId">Unique attachment identifier to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if attachment was deleted, false if not found</returns>
    Task<bool> DeleteAsync(int layerId, long featureId, long attachmentId, CancellationToken cancellationToken = default);

    // File operations

    /// <summary>
    /// Uploads attachment content and creates the attachment record
    /// </summary>
    /// <param name="layerId">Layer identifier containing the feature</param>
    /// <param name="featureId">Feature identifier that will own the attachment</param>
    /// <param name="filename">Original filename of the attachment</param>
    /// <param name="contentType">MIME content type of the file</param>
    /// <param name="content">File content stream</param>
    /// <param name="keywords">Optional keywords for the attachment</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created attachment with file stored</returns>
    Task<Attachment> UploadAsync(int layerId, long featureId, string filename, string contentType, Stream content, string? keywords = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads attachment content
    /// </summary>
    /// <param name="layerId">Layer identifier containing the feature</param>
    /// <param name="featureId">Feature identifier that owns the attachment</param>
    /// <param name="attachmentId">Unique attachment identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>File content stream and attachment metadata, null if not found</returns>
    Task<AttachmentContent?> DownloadAsync(int layerId, long featureId, long attachmentId, CancellationToken cancellationToken = default);
}

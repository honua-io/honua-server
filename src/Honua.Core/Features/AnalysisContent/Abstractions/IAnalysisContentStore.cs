// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AnalysisContent.Domain;

namespace Honua.Core.Features.AnalysisContent.Abstractions;

/// <summary>
/// Raised when a durable analysis content write conflicts with an existing item,
/// version, or artifact record.
/// </summary>
public sealed class AnalysisContentStoreConflictException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AnalysisContentStoreConflictException"/> class.
    /// </summary>
    /// <param name="message">Safe conflict message.</param>
    public AnalysisContentStoreConflictException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AnalysisContentStoreConflictException"/> class.
    /// </summary>
    /// <param name="message">Safe conflict message.</param>
    /// <param name="innerException">Underlying provider exception.</param>
    public AnalysisContentStoreConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Raised when the durable analysis content store cannot be reached or used because of an
/// infrastructure outage (connection failure, timeout, resource exhaustion, or backend
/// authorization failure) rather than a request-level problem. Callers map this to a
/// retryable 503 so a transient store outage is not reported as a generic 500.
/// </summary>
public sealed class AnalysisContentStoreUnavailableException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AnalysisContentStoreUnavailableException"/> class.
    /// </summary>
    /// <param name="message">Safe, store-neutral unavailability message.</param>
    public AnalysisContentStoreUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AnalysisContentStoreUnavailableException"/> class.
    /// </summary>
    /// <param name="message">Safe, store-neutral unavailability message.</param>
    /// <param name="innerException">Underlying provider exception.</param>
    public AnalysisContentStoreUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Durable store for saved-query and analysis-package content items, immutable versions,
/// and job result artifact metadata.
/// </summary>
public interface IAnalysisContentStore
{
    /// <summary>
    /// Creates a content item and its initial immutable version.
    /// </summary>
    /// <param name="item">Content item root.</param>
    /// <param name="version">Initial immutable version.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored item root.</returns>
    Task<AnalysisContentItem> CreateItemAsync(
        AnalysisContentItem item,
        AnalysisContentVersion version,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a content item root.
    /// </summary>
    /// <param name="itemId">Content item identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The item, or null when not found.</returns>
    Task<AnalysisContentItem?> GetItemAsync(
        string itemId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds an immutable version and advances the item current-version pointer.
    /// </summary>
    /// <param name="itemId">Content item identifier.</param>
    /// <param name="version">Version to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored version.</returns>
    Task<AnalysisContentVersion> AddVersionAsync(
        string itemId,
        AnalysisContentVersion version,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the latest or explicit version for a content item.
    /// </summary>
    /// <param name="itemId">Content item identifier.</param>
    /// <param name="version">Explicit version, or null for the latest version.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The version, or null when not found.</returns>
    Task<AnalysisContentVersion?> GetVersionAsync(
        string itemId,
        int? version = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all immutable versions for a content item in ascending version order.
    /// </summary>
    /// <param name="itemId">Content item identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Stored versions.</returns>
    Task<IReadOnlyList<AnalysisContentVersion>> ListVersionsAsync(
        string itemId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts or updates durable result artifact metadata.
    /// </summary>
    /// <param name="artifact">Artifact metadata record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored artifact record.</returns>
    Task<ResultArtifactRecord> UpsertArtifactAsync(
        ResultArtifactRecord artifact,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves durable result artifact metadata.
    /// </summary>
    /// <param name="artifactId">Artifact identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The artifact, or null when not found.</returns>
    Task<ResultArtifactRecord?> GetArtifactAsync(
        string artifactId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists artifacts created by a job.
    /// </summary>
    /// <param name="jobId">Job identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Artifact records in creation order.</returns>
    Task<IReadOnlyList<ResultArtifactRecord>> ListArtifactsForJobAsync(
        string jobId,
        CancellationToken cancellationToken = default);
}

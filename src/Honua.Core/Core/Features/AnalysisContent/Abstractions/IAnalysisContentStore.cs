// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AnalysisContent.Domain;

namespace Honua.Core.Features.AnalysisContent.Abstractions;

public sealed class AnalysisContentStoreConflictException : Exception
{
    public AnalysisContentStoreConflictException(string message)
        : base(message)
    {
    }

    public AnalysisContentStoreConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

// Callers map an infrastructure outage to a retryable 503 so a transient store failure is not reported as a generic 500.
public sealed class AnalysisContentStoreUnavailableException : Exception
{
    public AnalysisContentStoreUnavailableException(string message)
        : base(message)
    {
    }

    public AnalysisContentStoreUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class AnalysisContentStoreValidationException : Exception
{
    public AnalysisContentStoreValidationException(string message, string code)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public interface IAnalysisContentStore
{
    Task<AnalysisContentItem> CreateItemAsync(
        AnalysisContentItem item,
        AnalysisContentVersion version,
        CancellationToken cancellationToken = default);

    Task<AnalysisContentItem?> GetItemAsync(
        string itemId,
        CancellationToken cancellationToken = default);

    Task<AnalysisContentVersion> AddVersionAsync(
        string itemId,
        AnalysisContentVersion version,
        CancellationToken cancellationToken = default);

    Task<AnalysisContentVersion?> GetVersionAsync(
        string itemId,
        int? version = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AnalysisContentVersion>> ListVersionsAsync(
        string itemId,
        CancellationToken cancellationToken = default);

    // Deleting the current version rewinds the pointer to the highest remaining version, and callers never abandon the only remaining version.
    Task<bool> TryAbandonVersionAsync(
        string itemId,
        int version,
        CancellationToken cancellationToken = default);

    Task<AnalysisContentItemPage> ListItemsAsync(
        AnalysisContentItemQuery query,
        CancellationToken cancellationToken = default);

    Task<ResultArtifactRecord> UpsertArtifactAsync(
        ResultArtifactRecord artifact,
        CancellationToken cancellationToken = default);

    Task<ResultArtifactRecord?> GetArtifactAsync(
        string artifactId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ResultArtifactRecord>> ListArtifactsForJobAsync(
        string jobId,
        CancellationToken cancellationToken = default);
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Share.Domain;

namespace Honua.Core.Features.Share.Abstractions;

public sealed class ShareExportStoreConflictException : Exception
{
    public ShareExportStoreConflictException(string message)
        : base(message)
    {
    }

    public ShareExportStoreConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

// An infrastructure outage is a retryable 503, not a generic 500.
public sealed class ShareExportStoreUnavailableException : Exception
{
    public ShareExportStoreUnavailableException(string message)
        : base(message)
    {
    }

    public ShareExportStoreUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public interface IShareExportStore
{
    Task<ShareExportDefinitionPage> ListDefinitionsAsync(
        ShareExportDefinitionQuery query,
        CancellationToken cancellationToken = default);

    Task<ShareExportDefinition?> GetDefinitionAsync(
        string exportId,
        CancellationToken cancellationToken = default);

    Task<ShareExportDefinition> CreateDefinitionAsync(
        ShareExportDefinition definition,
        CancellationToken cancellationToken = default);

    Task<ShareExportDefinition?> UpdateDefinitionAsync(
        ShareExportDefinition definition,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteDefinitionAsync(
        string exportId,
        CancellationToken cancellationToken = default);

    Task<ShareExportRun> AppendRunAsync(
        ShareExportRun run,
        CancellationToken cancellationToken = default);

    Task<ShareExportRun?> UpdateRunAsync(
        ShareExportRun run,
        CancellationToken cancellationToken = default);

    Task<ShareExportRunPage> ListRunsAsync(
        string exportId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default);

    Task<ShareExportRun?> GetRunAsync(
        string exportId,
        string runId,
        CancellationToken cancellationToken = default);
}

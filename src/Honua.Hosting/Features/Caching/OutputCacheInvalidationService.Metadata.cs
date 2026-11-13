// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.Infrastructure.Caching;

internal sealed partial class OutputCacheInvalidationService
{
    private async Task<MetadataV2GraphSnapshot?> ReadMetadataSnapshotAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var graphProvider = scope.ServiceProvider.GetService<IMetadataV2GraphProvider>();
        return graphProvider is null ? null : await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<MetadataV2GraphSnapshot?> ReadStorageInvalidationSnapshotAsync(
        int storageLayerId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadMetadataSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogReadStorageMetadataFailed(_logger, storageLayerId, exception);
            return null;
        }
    }

    [LoggerMessage(EventId = 4518, Level = LogLevel.Warning,
        Message = "Failed to read metadata for storage layer {StorageLayerId} during cache invalidation")]
    private static partial void LogReadStorageMetadataFailed(ILogger logger, int storageLayerId, Exception exception);
}

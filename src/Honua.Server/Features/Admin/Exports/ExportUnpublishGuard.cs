// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Admin.TileOperations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.Exports;

internal readonly record struct ExportUnpublishFence(string? InitialETag, DateTimeOffset? JobCreatedAt = null);

// Single fence shared by retry endpoints and execution-core activation so an unpublished Export
// cannot be revived by retrying an older job, while a fresh POST /admin/exports still republishes.
internal static class ExportUnpublishGuard
{
    public static async Task<string?> TryGetRetryBlockedMessageAsync(
        IServiceProvider services,
        ExecutionJobRecord job,
        CancellationToken cancellationToken)
    {
        if (job.Spec.Kind != ExecutionJobKind.TileCache
            || !TileCacheExecutionSpecBuilder.TryParse(job.Spec.Parameters, out var request, out _, out _)
            || !string.Equals(request.Operation, TileCacheExecutionSpecBuilder.ExportOperation, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(request.ExportId))
        {
            return null;
        }

        var storage = services.GetService<ICloudFileStorage>();
        var storageOptions = services.GetService<IOptions<CloudStorageOptions>>();
        if (storage is null || storageOptions is null)
        {
            return null;
        }

        var store = ExportStore.Create(storage, storageOptions.Value, request.ExportId);
        var marker = await store.ReadUnpublishedAsync(cancellationToken).ConfigureAwait(false);
        if (marker is not null && job.CreatedAt <= marker.UnpublishedAt)
        {
            return $"Export '{request.ExportId}' was unpublished; start a new export with POST /api/v1/admin/exports instead of retrying an older job.";
        }

        return null;
    }

    public static async Task RequireNotUnpublishedAsync(
        ExportStore store,
        ExportUnpublishFence fence,
        CancellationToken cancellationToken)
    {
        var currentUnpublishedETag = await store.ReadUnpublishedETagAsync(cancellationToken).ConfigureAwait(false);
        if (currentUnpublishedETag is null)
        {
            return;
        }

        if (!string.Equals(currentUnpublishedETag, fence.InitialETag, StringComparison.Ordinal))
        {
            throw new ExportException("This Export was unpublished while the job was running; its version was not activated.");
        }

        if (fence.JobCreatedAt is { } createdAt)
        {
            var marker = await store.ReadUnpublishedAsync(cancellationToken).ConfigureAwait(false);
            if (marker is not null && createdAt <= marker.UnpublishedAt)
            {
                throw new ExportException("This Export was unpublished after the job was created; its version was not activated.");
            }
        }
    }

    public static async Task TryClearUnpublishedIfUnchangedAsync(ExportStore store, string expectedETag)
    {
        try
        {
            var currentETag = await store.ReadUnpublishedETagAsync(CancellationToken.None).ConfigureAwait(false);
            if (string.Equals(currentETag, expectedETag, StringComparison.Ordinal))
            {
                await store.ClearUnpublishedAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Pointer switch already succeeded; stale marker cleanup is best-effort.
        }
    }
}

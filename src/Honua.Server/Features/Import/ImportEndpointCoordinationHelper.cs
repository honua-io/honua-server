// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Server.Features.Infrastructure.Helpers;

namespace Honua.Server.Features.Import;

internal static class ImportEndpointCoordinationHelper
{
    public static async Task<bool> PersistQueuedJobStateAsync<TRequest, TProgress>(
        HttpContext context,
        bool canAcceptNewJobs,
        IDistributedProgressStore<TRequest> requestStore,
        IDistributedProgressStore<TProgress> progressStore,
        string jobId,
        TRequest request,
        TProgress progress,
        TimeSpan ttl,
        string unavailableMessage,
        CancellationToken cancellationToken)
        where TRequest : class
        where TProgress : class
    {
        await requestStore.SetProgressAsync(jobId, request, ttl, cancellationToken).ConfigureAwait(false);
        if (!await EnsureCoordinationStillDurableAsync(
                context,
                canAcceptNewJobs,
                requestStore,
                progressStore,
                jobId,
                unavailableMessage,
                cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await progressStore.SetProgressAsync(jobId, progress, ttl, cancellationToken).ConfigureAwait(false);
        return await EnsureCoordinationStillDurableAsync(
            context,
            canAcceptNewJobs,
            requestStore,
            progressStore,
            jobId,
            unavailableMessage,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<bool> EnsureCoordinationStillDurableAsync<TRequest, TProgress>(
        HttpContext context,
        bool canAcceptNewJobs,
        IDistributedProgressStore<TRequest> requestStore,
        IDistributedProgressStore<TProgress> progressStore,
        string jobId,
        string unavailableMessage,
        CancellationToken cancellationToken)
        where TRequest : class
        where TProgress : class
    {
        if (canAcceptNewJobs)
        {
            return true;
        }

        await DeleteQueuedStateAsync(requestStore, progressStore, jobId, cancellationToken).ConfigureAwait(false);
        await AdminResponseWriter.WriteErrorAsync(
            context,
            unavailableMessage,
            StatusCodes.Status503ServiceUnavailable);
        return false;
    }

    public static async Task DeleteQueuedStateAsync<TRequest, TProgress>(
        IDistributedProgressStore<TRequest> requestStore,
        IDistributedProgressStore<TProgress> progressStore,
        string jobId,
        CancellationToken cancellationToken)
        where TRequest : class
        where TProgress : class
    {
        await requestStore.DeleteProgressAsync(jobId, cancellationToken).ConfigureAwait(false);
        await progressStore.DeleteProgressAsync(jobId, cancellationToken).ConfigureAwait(false);
    }
}

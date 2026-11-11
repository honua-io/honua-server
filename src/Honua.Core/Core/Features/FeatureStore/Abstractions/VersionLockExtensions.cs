// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Abstractions;

public readonly record struct VersionLockRequest(string Service, Guid VersionId, TimeSpan LeaseDuration);

public static class VersionLockExtensions
{
    // Losing the lock cancels the work, which then reports the version as locked: another holder
    // may already be maintaining it, so the caller retries rather than continuing unprotected.
    public static async Task<TResult> RunExclusiveAsync<TResult>(
        this IVersionLock versionLock,
        VersionLockRequest request,
        Func<CancellationToken, Task<TResult>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(versionLock);
        ArgumentNullException.ThrowIfNull(work);

        await using var handle = await versionLock
            .TryAcquireAsync(request.Service, request.VersionId, request.LeaseDuration, cancellationToken)
            .ConfigureAwait(false) ?? throw new VersionLockedException(request.VersionId);
        using var whileHeld = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, handle.LockLostToken);
        try
        {
            return await work(whileHeld.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            handle.LockLostToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new VersionLockedException(request.VersionId);
        }
    }
}

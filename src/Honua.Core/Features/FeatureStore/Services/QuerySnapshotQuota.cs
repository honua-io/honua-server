// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Services;

/// <summary>
/// Global cap on unexpired durable query snapshots. The store enforces this
/// before insert so anonymous track-changes baselines cannot fill the table.
/// </summary>
public static class QuerySnapshotQuota
{
    /// <summary>Maximum unexpired snapshot rows.</summary>
    public const int MaxLiveSnapshots = 64;

    /// <summary>Maximum combined unexpired payload bytes.</summary>
    public const long MaxLiveBytes = 256L * 1024L * 1024L;

    /// <summary>
    /// Rejects an insert that would exceed the live row count or byte budget.
    /// </summary>
    public static void Ensure(int liveCount, long liveBytes, int incomingBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(liveCount);
        ArgumentOutOfRangeException.ThrowIfNegative(liveBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(incomingBytes);

        if (liveCount >= MaxLiveSnapshots || liveBytes > MaxLiveBytes - incomingBytes)
        {
            throw new QuerySnapshotQuotaExceededException(
                $"Durable query snapshots are limited to {MaxLiveSnapshots} unexpired receipts and {MaxLiveBytes} bytes.");
        }
    }
}

/// <summary>
/// Raised when a durable query snapshot would exceed <see cref="QuerySnapshotQuota"/>.
/// </summary>
public sealed class QuerySnapshotQuotaExceededException(string message) : Exception(message);

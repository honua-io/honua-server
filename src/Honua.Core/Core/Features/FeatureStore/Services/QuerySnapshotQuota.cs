// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Services;

public static class QuerySnapshotQuota
{
    public const int MaxLiveSnapshots = 64;

    public const long MaxLiveBytes = 256L * 1024L * 1024L;

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

public sealed class QuerySnapshotQuotaExceededException(string message) : Exception(message);

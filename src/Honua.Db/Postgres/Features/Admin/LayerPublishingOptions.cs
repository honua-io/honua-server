// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Db.Postgres.Features.Admin;

internal sealed class LayerPublishingOptions
{
    public const string SectionName = "LayerPublishing";

    public const int DefaultMaterializationTimeoutSeconds = 300;

    public const int MaximumMaterializationTimeoutSeconds = 3600;

    public int MaterializationTimeoutSeconds { get; set; } = DefaultMaterializationTimeoutSeconds;

    public const int DefaultAsyncPublishRowThreshold = 10_000;

    public const int DefaultMaterializationBatchSize = 5_000;

    public int AsyncPublishRowThreshold { get; set; } = DefaultAsyncPublishRowThreshold;

    public int MaterializationBatchSize { get; set; } = DefaultMaterializationBatchSize;

    internal static bool IsValidMaterializationTimeout(int seconds)
        => seconds is >= 1 and <= MaximumMaterializationTimeoutSeconds;

    internal static bool IsValidAsyncPublishRowThreshold(int threshold) => threshold >= 0;

    internal static bool IsValidMaterializationBatchSize(int batchSize) => batchSize >= 1;

    internal int GetValidatedMaterializationTimeoutSeconds()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaterializationTimeoutSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            MaterializationTimeoutSeconds, MaximumMaterializationTimeoutSeconds);
        return MaterializationTimeoutSeconds;
    }

    internal int GetValidatedAsyncPublishRowThreshold()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(AsyncPublishRowThreshold);
        return AsyncPublishRowThreshold;
    }

    internal int GetValidatedMaterializationBatchSize()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaterializationBatchSize, 1);
        return MaterializationBatchSize;
    }
}

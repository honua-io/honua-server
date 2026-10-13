// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Db.Postgres.Features.Admin;

/// <summary>Budgets for PostgreSQL layer publication and snapshot refresh.</summary>
internal sealed class LayerPublishingOptions
{
    /// <summary>Configuration section for layer publication.</summary>
    public const string SectionName = "LayerPublishing";

    /// <summary>Default snapshot-copy budget, in seconds.</summary>
    public const int DefaultMaterializationTimeoutSeconds = 300;

    /// <summary>Maximum supported snapshot-copy budget, in seconds.</summary>
    public const int MaximumMaterializationTimeoutSeconds = 3600;

    /// <summary>
    /// Wall-clock and database command budget for one canonical snapshot copy (and the delete
    /// that precedes a refresh). Applies to publication and refresh, not ordinary feature
    /// queries. Must be between one second and one hour; caller cancellation remains effective.
    /// </summary>
    public int MaterializationTimeoutSeconds { get; set; } = DefaultMaterializationTimeoutSeconds;

    /// <summary>
    /// Source row count above which an accepted publish is queued instead of completed in the
    /// request (honua-server#5948). Zero queues every non-empty table. A count equal to the
    /// threshold stays synchronous.
    /// </summary>
    public const int DefaultAsyncPublishRowThreshold = 10_000;

    /// <summary>Rows copied per snapshot statement inside the publication transaction.</summary>
    public const int DefaultMaterializationBatchSize = 5_000;

    /// <summary>
    /// Source row count above which publish returns a pollable job. Must be zero or greater.
    /// </summary>
    public int AsyncPublishRowThreshold { get; set; } = DefaultAsyncPublishRowThreshold;

    /// <summary>
    /// Keyset page size for the canonical snapshot copy. Must be at least one.
    /// </summary>
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

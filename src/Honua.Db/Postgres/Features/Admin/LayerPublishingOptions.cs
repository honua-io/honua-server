// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Db.Postgres.Features.Admin;

/// <summary>Budgets for PostgreSQL layer publication and snapshot refresh.</summary>
internal sealed class LayerPublishingOptions
{
    /// <summary>Configuration section for layer publication.</summary>
    public const string SectionName = "LayerPublishing";

    /// <summary>Maximum supported snapshot-copy budget, in seconds.</summary>
    public const int MaximumMaterializationTimeoutSeconds = 3600;

    /// <summary>
    /// Wall-clock and database command budget for one canonical snapshot copy.
    /// Applies to publication and refresh, not ordinary feature queries. Must be
    /// between one second and one hour; caller cancellation remains effective.
    /// </summary>
    public int MaterializationTimeoutSeconds { get; set; } = 300;

    internal int GetValidatedMaterializationTimeoutSeconds()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaterializationTimeoutSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            MaterializationTimeoutSeconds, MaximumMaterializationTimeoutSeconds);
        return MaterializationTimeoutSeconds;
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.ObjectModel;
using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Services;

/// <summary>
/// Seed registry of <see cref="MigrationMetricBaseline"/> entries for migration cost
/// and performance evidence (issue #1033 slice 2).
/// </summary>
/// <remarks>
/// <para>
/// Slice 2 seeds GeoServer Small only. ArcGIS, OGC Features, OGC map/tile metadata,
/// and Coverage baselines are intentionally deferred to later slices so each can
/// be tuned against a real fixture run. Callers must handle <c>TryGet</c> returning
/// <c>false</c> until those slices land.
/// </para>
/// </remarks>
public static class MigrationFixtureBaselineCatalog
{
    private static readonly ReadOnlyDictionary<string, MigrationMetricBaseline> Seeded = BuildSeed();

    /// <summary>Number of baselines currently seeded. Used by tests to assert the seed cut.</summary>
    public static int SeededCount => Seeded.Count;

    /// <summary>
    /// Lookup the seeded baseline for a source family + fixture size pair.
    /// </summary>
    public static bool TryGet(string sourceFamily, string size, out MigrationMetricBaseline baseline)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFamily);
        ArgumentException.ThrowIfNullOrWhiteSpace(size);
        return Seeded.TryGetValue(Key(sourceFamily, size), out baseline!);
    }

    /// <summary>Enumerate all seeded baselines in deterministic order.</summary>
    public static IReadOnlyCollection<MigrationMetricBaseline> All() => Seeded.Values;

    private static string Key(string sourceFamily, string size)
        => $"{sourceFamily.Trim().ToLowerInvariant()}::{size.Trim().ToLowerInvariant()}";

    private static ReadOnlyDictionary<string, MigrationMetricBaseline> BuildSeed()
    {
        var dict = new Dictionary<string, MigrationMetricBaseline>(StringComparer.Ordinal);
        var geoServerSmall = GeoServerSmallBaseline();
        dict[Key(geoServerSmall.SourceFamily, geoServerSmall.Size)] = geoServerSmall;
        return new ReadOnlyDictionary<string, MigrationMetricBaseline>(dict);
    }

    /// <summary>
    /// Initial GeoServer Small baseline. Expected envelope and bands are calibrated for
    /// a small deterministic GeoServer fixture (≤20 workspaces / ≤200 layers /
    /// ≤10k features / ≤60s wall clock).
    /// </summary>
    public static MigrationMetricBaseline GeoServerSmallBaseline() => new()
    {
        ProfileName = "geoserver-small-v1",
        SourceFamily = MigrationCostPerformanceSourceFamilies.GeoServerRest,
        Size = MigrationCostPerformanceFixtureSizes.Small,
        FixtureProfile = new MigrationFixtureSizeProfile
        {
            SourceFamily = MigrationCostPerformanceSourceFamilies.GeoServerRest,
            Size = MigrationCostPerformanceFixtureSizes.Small,
            Description = "Deterministic GeoServer fixture: up to 20 workspaces, 200 layers, 10k features, 60s wall clock.",
            ExpectedResourceCount = 200,
            ExpectedFeatureCount = 10_000,
            ExpectedCoverageCount = 0,
            ExpectedDurationMilliseconds = 60_000,
            ExpectedBytesRead = 64L * 1024 * 1024,
            ExpectedBytesWritten = 64L * 1024 * 1024,
            ExpectedSourceRequestCount = 400
        },
        Bands =
        [
            new MigrationMetricBaselineBand
            {
                Metric = "durationMilliseconds",
                Unit = "milliseconds",
                WarnAtOrAbove = 60_000,
                FailAtOrAbove = 180_000
            },
            new MigrationMetricBaselineBand
            {
                Metric = "sourceRequestCount",
                Unit = "count",
                WarnAtOrAbove = 600,
                FailAtOrAbove = 2_000
            },
            new MigrationMetricBaselineBand
            {
                Metric = "bytesRead",
                Unit = "bytes",
                WarnAtOrAbove = 128L * 1024 * 1024,
                FailAtOrAbove = 512L * 1024 * 1024
            },
            new MigrationMetricBaselineBand
            {
                Metric = "bytesWritten",
                Unit = "bytes",
                WarnAtOrAbove = 128L * 1024 * 1024,
                FailAtOrAbove = 512L * 1024 * 1024
            },
            new MigrationMetricBaselineBand
            {
                Metric = "retryCount",
                Unit = "count",
                WarnAtOrAbove = 1,
                FailAtOrAbove = 10
            },
            new MigrationMetricBaselineBand
            {
                Metric = "resumeCount",
                Unit = "count",
                WarnAtOrAbove = 1,
                FailAtOrAbove = 5
            },
            new MigrationMetricBaselineBand
            {
                Metric = "manualReviewRatio",
                Unit = "ratio",
                WarnAtOrAbove = 0.10,
                FailAtOrAbove = 0.25
            },
            new MigrationMetricBaselineBand
            {
                Metric = "featureThroughputPerSecond",
                Unit = "features/second",
                LowerBound = true,
                WarnAtOrBelow = 50,
                FailAtOrBelow = 10
            }
        ]
    };
}

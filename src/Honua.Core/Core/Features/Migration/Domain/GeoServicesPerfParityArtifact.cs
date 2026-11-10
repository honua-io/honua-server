// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Domain;

public static class PerfParityVerdicts
{
    public const string Pass = "Pass";

    public const string Warn = "Warn";

    public const string Fail = "Fail";

    public const string Unknown = "Unknown";
}

public static class PerfParityMetrics
{
    public const string P95Ratio = "p95Ratio";

    public const string P99Ratio = "p99Ratio";
}

// A ratio at or above the warning budget warns and a ratio at or above the failure budget fails; null thresholds disable grading.
public sealed record PerfParityBudget
{
    public required string ProfileName { get; init; }

    public double? WarnP95RatioAtOrAbove { get; init; }

    public double? FailP95RatioAtOrAbove { get; init; }

    public double? WarnP99RatioAtOrAbove { get; init; }

    public double? FailP99RatioAtOrAbove { get; init; }

    // Fewer samples than the minimum stay ungraded so a thin sample cannot pass or fail.
    public int MinimumSampleCount { get; init; } = 1;

    public static PerfParityBudget GeoServicesDefault { get; } = new()
    {
        ProfileName = "geoservices-parity-default-v1",
        WarnP95RatioAtOrAbove = 1.5d,
        FailP95RatioAtOrAbove = 2.0d,
        WarnP99RatioAtOrAbove = 1.75d,
        FailP99RatioAtOrAbove = 2.5d,
        MinimumSampleCount = 5
    };
}

public sealed record PerfParitySignal
{
    public required string Metric { get; init; }

    public required string Verdict { get; init; }

    public double? ObservedRatio { get; init; }

    public double? WarnThreshold { get; init; }

    public double? FailThreshold { get; init; }

    public required string Summary { get; init; }
}

public sealed record PerfParityVerdict
{
    public string ArtifactKind { get; init; } = "honua.migration.perf-parity-verdict";

    public string ArtifactVersion { get; init; } = "1.0";

    public required string BudgetProfile { get; init; }

    public required string Verdict { get; init; }

    public required string Summary { get; init; }

    public int SampleCount { get; init; }

    public PerfParitySignal[] Signals { get; init; } = [];
}

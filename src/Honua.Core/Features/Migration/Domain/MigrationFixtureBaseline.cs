// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
namespace Honua.Core.Features.Migration.Domain;

public static class MigrationMetricBaselineStatuses
{
    public const string Pass = "Pass";

    public const string Warn = "Warn";

    public const string Fail = "Fail";
}

public sealed record MigrationFixtureSizeProfile
{
    public required string SourceFamily { get; init; }

    public required string Size { get; init; }

    public required string Description { get; init; }

    public long? ExpectedResourceCount { get; init; }

    public long? ExpectedFeatureCount { get; init; }

    public long? ExpectedCoverageCount { get; init; }

    public long? ExpectedDurationMilliseconds { get; init; }

    public long? ExpectedBytesRead { get; init; }

    public long? ExpectedBytesWritten { get; init; }

    public long? ExpectedSourceRequestCount { get; init; }
}

public sealed record MigrationMetricBaselineBand
{
    public required string Metric { get; init; }

    public required string Unit { get; init; }

    public double? WarnAtOrAbove { get; init; }

    public double? FailAtOrAbove { get; init; }

    public double? WarnAtOrBelow { get; init; }

    public double? FailAtOrBelow { get; init; }

    public bool LowerBound { get; init; }
}

public sealed record MigrationMetricBaseline
{
    public required string ProfileName { get; init; }

    public required string SourceFamily { get; init; }

    public required string Size { get; init; }

    public required MigrationFixtureSizeProfile FixtureProfile { get; init; }

    public MigrationMetricBaselineBand[] Bands { get; init; } = [];
}

public sealed record MigrationMetricBaselineSignal
{
    public required string Metric { get; init; }

    public required string Status { get; init; }

    public required string Unit { get; init; }

    public double? Observed { get; init; }

    public double? WarnThreshold { get; init; }

    public double? FailThreshold { get; init; }

    public required string Summary { get; init; }
}

public sealed record MigrationRunMetricsBaselineArtifact
{
    public string ArtifactKind { get; init; } = "honua.migration.run-metrics-baseline";

    public string ArtifactVersion { get; init; } = "1.0";

    public required string SourceFamily { get; init; }

    public required string Size { get; init; }

    public required string BaselineProfile { get; init; }

    public required string Status { get; init; }

    public required string Summary { get; init; }

    public string? RunId { get; init; }

    public required string MeasurementScope { get; init; }

    public MigrationMetricBaselineSignal[] Signals { get; init; } = [];

    public required MigrationFixtureSizeProfile FixtureProfile { get; init; }
}

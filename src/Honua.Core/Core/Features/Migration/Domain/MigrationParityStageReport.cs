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

public sealed record MigrationParityStageReport
{
    public string ArtifactKind { get; init; } = "honua.migration.parity-stage-report";

    public string ArtifactVersion { get; init; } = "1.0";

    public required string RunId { get; init; }

    public required string ApplyRunId { get; init; }

    public required MigrationParityStageSummary Summary { get; init; }

    public MigrationParityStageEntry[] Sources { get; init; } = [];
}

public sealed record MigrationParityStageEntry
{
    public required string FixtureId { get; init; }

    public required string SourceKind { get; init; }

    public required string Classification { get; init; }

    public required MigrationParityStageOutcome Outcome { get; init; }
}

public sealed record MigrationParityStageOutcome
{
    public required MigrationParityEvidenceArtifact Evidence { get; init; }

    public required string ReplayToken { get; init; }

    public int ResourceCount { get; init; }

    public int FeatureCountMatchCount { get; init; }

    public int FeatureCountMismatchCount { get; init; }

    public int SchemaMatchCount { get; init; }

    public int SchemaMismatchCount { get; init; }

    public int BboxOverlapMatchCount { get; init; }

    public int BboxOverlapMismatchCount { get; init; }

    public int NotApplicableProbeCount { get; init; }

    public int ManualReviewResourceCount { get; init; }

    public MigrationParityResourceProbe[] ResourceProbes { get; init; } = [];

    public MigrationParityStageDiagnostic[] Diagnostics { get; init; } = [];
}

public sealed record MigrationParityResourceProbe
{
    public required string SourceResourceId { get; init; }

    public string? TargetResourceId { get; init; }

    public required string ManifestAction { get; init; }

    public required string Classification { get; init; }

    public required MigrationParityProbeResult FeatureCount { get; init; }

    public required MigrationParityProbeResult Schema { get; init; }

    public required MigrationParityProbeResult BboxOverlap { get; init; }
}

public sealed record MigrationParityProbeResult
{
    public required string ProbeId { get; init; }

    public required string State { get; init; }

    // The summary must not contain credentials.
    public required string Summary { get; init; }

    public string? Expected { get; init; }

    public string? Observed { get; init; }

    public string[] Differences { get; init; } = [];
}

public sealed record MigrationParityStageDiagnostic
{
    public required string Code { get; init; }

    public required string Severity { get; init; }

    public required string SourceId { get; init; }

    // The diagnostic must not contain credentials or other secrets.
    public required string Message { get; init; }
}

public sealed record MigrationParityStageSummary
{
    public int SourceCount { get; init; }

    public int PassSourceCount { get; init; }

    public int WarnSourceCount { get; init; }

    public int FailSourceCount { get; init; }

    public int ManualReviewSourceCount { get; init; }

    public int ResourceCount { get; init; }

    public int FeatureCountMatchCount { get; init; }

    public int FeatureCountMismatchCount { get; init; }

    public int SchemaMatchCount { get; init; }

    public int SchemaMismatchCount { get; init; }

    public int BboxOverlapMatchCount { get; init; }

    public int BboxOverlapMismatchCount { get; init; }

    public int DiagnosticCount { get; init; }
}

public sealed record MigrationParityTargetObservation
{
    public required string SourceResourceId { get; init; }

    public long? FeatureCount { get; init; }

    public string[]? FieldNames { get; init; }

    public double[]? Bbox { get; init; }
}

public sealed record MigrationParitySourceObservation
{
    public required string SourceResourceId { get; init; }

    public long? FeatureCount { get; init; }

    public string[]? FieldNames { get; init; }

    public double[]? Bbox { get; init; }
}

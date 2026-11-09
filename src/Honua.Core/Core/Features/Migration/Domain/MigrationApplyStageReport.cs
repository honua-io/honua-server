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

public sealed record MigrationApplyStageReport
{
    public string ArtifactKind { get; init; } = "honua.migration.apply-stage-report";

    public string ArtifactVersion { get; init; } = "1.0";

    public required string RunId { get; init; }

    public required string ScanRunId { get; init; }

    public required MigrationApplyStageSummary Summary { get; init; }

    public MigrationApplyStageEntry[] Sources { get; init; } = [];
}

public sealed record MigrationApplyStageEntry
{
    public required string FixtureId { get; init; }

    public required string SourceKind { get; init; }

    public required string ApplyMode { get; init; }

    public required MigrationApplyStageOutcome Outcome { get; init; }
}

public sealed record MigrationApplyStageOutcome
{
    public required MigrationManifestArtifact Manifest { get; init; }

    public required string ReplayToken { get; init; }

    public int AppliedItemCount { get; init; }

    public int ManualReviewItemCount { get; init; }

    public int UnsupportedItemCount { get; init; }

    public MigrationApplyStageItemClassification[] Classifications { get; init; } = [];

    public MigrationManifestReviewItem[] ManualReviewItems { get; init; } = [];

    public MigrationApplyStageDiagnostic[] Diagnostics { get; init; } = [];
}

public sealed record MigrationApplyStageItemClassification
{
    public required string SourceId { get; init; }

    public required string Kind { get; init; }

    public required string Disposition { get; init; }

    public required string Action { get; init; }
}

public sealed record MigrationApplyStageDiagnostic
{
    public required string Code { get; init; }

    public required string Severity { get; init; }

    public required string SourceId { get; init; }

    public required string Message { get; init; }
}

public sealed record MigrationApplyStageSummary
{
    public int SourceCount { get; init; }

    public int AppliedSourceCount { get; init; }

    public int DryRunSourceCount { get; init; }

    public int AppliedItemCount { get; init; }

    public int ManualReviewItemCount { get; init; }

    public int UnsupportedItemCount { get; init; }

    public int DiagnosticCount { get; init; }
}

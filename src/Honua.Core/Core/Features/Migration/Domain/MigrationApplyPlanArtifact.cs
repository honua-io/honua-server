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

public sealed record MigrationApplyPlanArtifact
{
    public string ArtifactKind { get; init; } = "honua.migration.apply-plan";

    public string ArtifactVersion { get; init; } = "1.0";

    public string SourceManifestArtifactKind { get; init; } = "honua.migration.manifest";

    public string SourceManifestArtifactVersion { get; init; } = "1.0";

    public required string SourceKind { get; init; }

    public required MigrationSourceIdentity Source { get; init; }

    public required string ReplayToken { get; init; }

    public required string PlanFingerprint { get; init; }

    public required MigrationApplyPlanSummary Summary { get; init; }

    public MigrationApplyPlanStep[] Steps { get; init; } = [];

    public MigrationManifestReviewItem[] ManualReviewItems { get; init; } = [];

    public MigrationManifestReviewItem[] UnsupportedItems { get; init; } = [];
}

public sealed record MigrationApplyPlanSummary
{
    public int TotalStepCount { get; init; }

    public int ReadyStepCount { get; init; }

    public int ManualReviewStepCount { get; init; }

    public int UnsupportedStepCount { get; init; }

    public int UnsupportedItemCount { get; init; }
}

public sealed record MigrationApplyPlanStep
{
    public int Sequence { get; init; }

    public required string StepId { get; init; }

    public required string SourceId { get; init; }

    public required string Kind { get; init; }

    public required string Action { get; init; }

    public required string Disposition { get; init; }

    public string? TargetServiceName { get; init; }

    public string? TargetPublicName { get; init; }

    public string? TargetFolderPath { get; init; }

    public GeoservicesImportPlan? ImportPlan { get; init; }

    public string? TargetResourceName { get; init; }

    public string[] StyleIds { get; init; } = [];

    public string[] ExternalDependencyIds { get; init; } = [];

    public string[] ReviewCodes { get; init; } = [];

    public MigrationStyleDiff[]? StyleDiffs { get; init; }

    public MigrationSecurityRuleDisposition[]? SecurityRules { get; init; }

    public required MigrationCompatibilityAssessment Compatibility { get; init; }
}

public sealed record MigrationStyleDiff
{
    public required string StyleId { get; init; }

    public required string StyleName { get; init; }

    public string Format { get; init; } = "sld";

    public string[] UnsupportedConstructs { get; init; } = [];

    public string[] Warnings { get; init; } = [];

    public string[] Errors { get; init; } = [];
}

public sealed record MigrationSecurityRuleDisposition
{
    public required string RuleKey { get; init; }

    public required string Mode { get; init; }

    public string[] Roles { get; init; } = [];

    public required string Status { get; init; }

    public string? Reason { get; init; }
}

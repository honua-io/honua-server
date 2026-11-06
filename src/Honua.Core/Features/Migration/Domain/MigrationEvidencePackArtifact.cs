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

// The fingerprint hashes canonical JSON and excludes timestamps and the generator label, and the pack never includes credentials, raw style bodies, or feature payloads.
public sealed record MigrationEvidencePackArtifact
{
    public string ArtifactKind { get; init; } = "honua.migration.evidence-pack";

    public string ArtifactVersion { get; init; } = "1.0";

    public required string RunId { get; init; }

    public required string Generator { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }

    public required string BundleFingerprint { get; init; }

    public required MigrationEvidencePackBundle Bundle { get; init; }
}

public sealed record MigrationEvidencePackBundle
{
    public required string SourceKind { get; init; }

    public required MigrationSourceIdentity Source { get; init; }

    public required MigrationEvidencePackWorkspaceScope WorkspaceScope { get; init; }

    public required MigrationEvidencePackApplyIdentity Apply { get; init; }

    public required MigrationEvidencePackSummary Summary { get; init; }

    public MigrationEvidencePackStage[] Stages { get; init; } = [];

    public MigrationEvidencePackStyleDiagnostic[] StyleDiagnostics { get; init; } = [];

    public required MigrationSourceInventoryArtifact Inventory { get; init; }

    public required MigrationManifestArtifact Manifest { get; init; }
}

public sealed record MigrationEvidencePackWorkspaceScope
{
    public required bool Restricted { get; init; }

    public string[] WorkspaceNames { get; init; } = [];
}

public sealed record MigrationEvidencePackApplyIdentity
{
    public required string PlanFingerprint { get; init; }

    public required string ReplayToken { get; init; }

    public required string ExecutionMode { get; init; }
}

public sealed record MigrationEvidencePackSummary
{
    public int TotalStepCount { get; init; }

    public int AppliedStepCount { get; init; }

    public int AlreadyAppliedStepCount { get; init; }

    public int ManualReviewStepCount { get; init; }

    public int UnsupportedStepCount { get; init; }

    public int FailedStepCount { get; init; }

    public int StyleManualReviewCount { get; init; }
}

public sealed record MigrationEvidencePackStage
{
    public required string Id { get; init; }

    public int StepCount { get; init; }

    public int AppliedCount { get; init; }

    public int AlreadyAppliedCount { get; init; }

    public int ManualReviewCount { get; init; }

    public int UnsupportedCount { get; init; }

    public int FailedCount { get; init; }

    public MigrationApplyExecutionStepResult[] StepResults { get; init; } = [];
}

public sealed record MigrationEvidencePackStyleDiagnostic
{
    public required string SourceId { get; init; }

    public required string StepOutcome { get; init; }

    public required string Message { get; init; }
}

public static class MigrationEvidencePackStageIds
{
    public const string Catalog = "catalog";

    public const string Data = "data";

    public const string Style = "style";
}

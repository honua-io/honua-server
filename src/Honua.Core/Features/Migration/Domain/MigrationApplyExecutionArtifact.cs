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

public sealed record MigrationApplyExecutionArtifact
{
    public string ArtifactKind { get; init; } = "honua.migration.apply-execution";

    public string ArtifactVersion { get; init; } = "1.0";

    public required string SourceKind { get; init; }

    public required MigrationSourceIdentity Source { get; init; }

    public required string PlanFingerprint { get; init; }

    public required string ReplayToken { get; init; }

    public string ExecutionMode { get; init; } = "catalog-apply";

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }

    public required MigrationApplyExecutionSummary Summary { get; init; }

    public MigrationApplyExecutionStepResult[] StepResults { get; init; } = [];
}

public sealed record MigrationApplyExecutionSummary
{
    public int TotalStepCount { get; init; }

    public int AppliedStepCount { get; init; }

    public int AlreadyAppliedStepCount { get; init; }

    public int ManualReviewStepCount { get; init; }

    public int UnsupportedStepCount { get; init; }

    public int FailedStepCount { get; init; }
}

public sealed record MigrationApplyExecutionStepResult
{
    public required string StepId { get; init; }

    public required string SourceId { get; init; }

    public required string Kind { get; init; }

    public required string Action { get; init; }

    public required string Disposition { get; init; }

    public required string Outcome { get; init; }

    public required string Message { get; init; }

    public string? TargetServiceName { get; init; }

    public string? TargetResourceName { get; init; }

    public int? HonuaLayerId { get; init; }

    public string[] ReviewCodes { get; init; } = [];
}

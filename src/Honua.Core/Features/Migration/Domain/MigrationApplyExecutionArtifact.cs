// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.FileImport.Services.FileGdb;
namespace Honua.Core.Features.Migration.Domain;

/// <summary>
/// Deterministic evidence produced when an apply plan is executed against a
/// target Honua catalog.
/// </summary>
public sealed record MigrationApplyExecutionArtifact
{
    /// <summary>
    /// Stable artifact kind identifier.
    /// </summary>
    public string ArtifactKind { get; init; } = "honua.migration.apply-execution";

    /// <summary>
    /// Artifact schema version.
    /// </summary>
    public string ArtifactVersion { get; init; } = "1.0";

    /// <summary>
    /// Source kind identifier such as <c>geoserver-rest</c>.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// Identity and version information for the scanned source.
    /// </summary>
    public required MigrationSourceIdentity Source { get; init; }

    /// <summary>
    /// Fingerprint of the apply plan that was executed.
    /// </summary>
    public required string PlanFingerprint { get; init; }

    /// <summary>
    /// Replay token copied from the apply plan.
    /// </summary>
    public required string ReplayToken { get; init; }

    /// <summary>
    /// Execution mode used by this apply run.
    /// </summary>
    public string ExecutionMode { get; init; } = "catalog-apply";

    /// <summary>
    /// When execution began.
    /// </summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// When execution completed.
    /// </summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>
    /// Aggregate execution counts.
    /// </summary>
    public required MigrationApplyExecutionSummary Summary { get; init; }

    /// <summary>
    /// Per-step execution outcomes ordered by apply-plan sequence.
    /// </summary>
    public MigrationApplyExecutionStepResult[] StepResults { get; init; } = [];
}

/// <summary>
/// Aggregate counts for migration apply execution.
/// </summary>
public sealed record MigrationApplyExecutionSummary
{
    /// <summary>
    /// Total number of apply-plan steps considered.
    /// </summary>
    public int TotalStepCount { get; init; }

    /// <summary>
    /// Number of steps applied during this run.
    /// </summary>
    public int AppliedStepCount { get; init; }

    /// <summary>
    /// Number of steps that were already present in the target catalog.
    /// </summary>
    public int AlreadyAppliedStepCount { get; init; }

    /// <summary>
    /// Number of steps that remain explicit manual-review work.
    /// </summary>
    public int ManualReviewStepCount { get; init; }

    /// <summary>
    /// Number of steps that are unsupported by this apply path.
    /// </summary>
    public int UnsupportedStepCount { get; init; }

    /// <summary>
    /// Number of steps that failed unexpectedly.
    /// </summary>
    public int FailedStepCount { get; init; }
}

/// <summary>
/// Execution outcome for one apply-plan step.
/// </summary>
public sealed record MigrationApplyExecutionStepResult
{
    /// <summary>
    /// Stable apply-plan step identifier.
    /// </summary>
    public required string StepId { get; init; }

    /// <summary>
    /// Source manifest item identifier.
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>
    /// Source item kind.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Planned action from the apply plan.
    /// </summary>
    public required string Action { get; init; }

    /// <summary>
    /// Step disposition from the apply plan.
    /// </summary>
    public required string Disposition { get; init; }

    /// <summary>
    /// Execution outcome: <c>applied</c>, <c>already-applied</c>,
    /// <c>manual-review</c>, <c>unsupported</c>, or <c>failed</c>.
    /// </summary>
    public required string Outcome { get; init; }

    /// <summary>
    /// Human-readable, secret-safe execution explanation.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Target service name from the plan, when applicable.
    /// </summary>
    public string? TargetServiceName { get; init; }

    /// <summary>
    /// Target resource name from the plan, when applicable.
    /// </summary>
    public string? TargetResourceName { get; init; }

    /// <summary>
    /// Honua layer identifier when a catalog layer was created or found.
    /// </summary>
    public int? HonuaLayerId { get; init; }

    /// <summary>
    /// Manual-review or unsupported classification codes copied from the plan.
    /// </summary>
    public string[] ReviewCodes { get; init; } = [];
}

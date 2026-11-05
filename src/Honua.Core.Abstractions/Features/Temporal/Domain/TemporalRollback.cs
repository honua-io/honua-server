// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Temporal.Domain;

public enum TemporalRollbackState
{
    Supported = 0,

    Blocked = 1,

    ScriptRequired = 2,

    JobRequired = 3,

    Manual = 4
}

public enum TemporalRollbackFindingSeverity
{
    Info = 0,

    Warning = 1,

    Error = 2
}

public sealed record TemporalRollbackFinding(
    string Code,
    TemporalRollbackFindingSeverity Severity,
    string Message);

public sealed record TemporalRollbackPlan(
    string ServiceId,
    int LayerId,
    ResolvedTemporalCheckpoint TargetCheckpoint,
    long CurrentGeneration,
    TemporalRollbackState State,
    int AffectedFeatureCount,
    ImmutableArray<TemporalRollbackFinding> ValidationFindings,
    ImmutableArray<TemporalRollbackFinding> CompatibilityFindings,
    bool RequiresApproval);

public sealed record TemporalRollbackPlanRequest(
    TemporalCheckpoint TargetCheckpoint);

public sealed record TemporalRollbackExecuteRequest(
    TemporalCheckpoint TargetCheckpoint,
    bool Approved,
    string? Reason);

public sealed record TemporalRollbackJobHandle(
    string JobId,
    string ServiceId,
    int LayerId,
    ResolvedTemporalCheckpoint TargetCheckpoint,
    string Status);

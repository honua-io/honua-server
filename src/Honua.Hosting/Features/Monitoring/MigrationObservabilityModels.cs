// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Infrastructure.Monitoring;

internal sealed record MigrationObservabilityResponse
{
    public required string Status { get; init; }

    public required bool IsReady { get; init; }

    public required bool IsFailed { get; init; }

    public string? Message { get; init; }

    public bool PlanAvailable { get; init; }

    public bool UpgradeRequired { get; init; }

    public IReadOnlyList<string> PendingScripts { get; init; } = Array.Empty<string>();

    public IReadOnlyList<DatabaseMigrationScriptBinding> ScriptBindings { get; init; }
        = Array.Empty<DatabaseMigrationScriptBinding>();

    public string? ContractApprovalNonce { get; init; }

    public IReadOnlyList<string> ExecutedButNotDiscoveredScripts { get; init; } = Array.Empty<string>();

    public string? PlanError { get; init; }

    public MigrationBackupHookStatus? BackupHook { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }
}

public sealed class MigrationBackupHookStatus
{
    public bool Configured { get; init; }

    public bool RequiredForPendingSet { get; init; }

    public bool RanForPendingSet { get; init; }

    public bool? Succeeded { get; init; }

    public string? Outcome { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public long? DurationMilliseconds { get; init; }

    public int? ExitCode { get; init; }

    public string? Stderr { get; init; }

    public IReadOnlyList<string> PendingContractScripts { get; init; } = Array.Empty<string>();

    public string? MigrationRunId { get; init; }

    public string? CorrelationId { get; init; }
}

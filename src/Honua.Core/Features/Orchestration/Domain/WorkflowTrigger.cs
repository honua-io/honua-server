// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Orchestration.Domain;

/// <summary>
/// Declarative trigger definition for a workflow. A null trigger means manual-only.
/// </summary>
public sealed record WorkflowTrigger
{
    /// <summary>
    /// Kind of trigger that should fire runs.
    /// </summary>
    public required WorkflowTriggerKind Kind { get; init; }

    /// <summary>
    /// Standard 5-field cron expression evaluated in <see cref="TimeZone"/>.
    /// Required when <see cref="Kind"/> is <see cref="WorkflowTriggerKind.Cron"/>.
    /// </summary>
    public string? CronExpression { get; init; }

    /// <summary>
    /// IANA time zone identifier for cron evaluation. Defaults to UTC when unset.
    /// </summary>
    public string? TimeZone { get; init; }

    /// <summary>
    /// Whether the trigger is currently enabled. Disabled triggers are skipped by the scheduler.
    /// </summary>
    public bool Enabled { get; init; } = true;
}

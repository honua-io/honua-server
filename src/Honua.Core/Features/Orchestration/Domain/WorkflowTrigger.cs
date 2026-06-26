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
    /// Layer identifiers watched by a <see cref="WorkflowTriggerKind.ChangeFeed"/> trigger.
    /// When any of these layers' change generation advances past the durable change-feed
    /// cursor, the workflow fires once for the new generation. Required (non-empty) for
    /// change-feed triggers.
    /// </summary>
    public IReadOnlyList<int> WatchedLayerIds { get; init; } = [];

    /// <summary>
    /// Configuration for a <see cref="WorkflowTriggerKind.ObjectStore"/> trigger. Required for
    /// object-store triggers.
    /// </summary>
    public ObjectStoreTriggerConfig? ObjectStore { get; init; }

    /// <summary>
    /// Whether the trigger is currently enabled. Disabled triggers are skipped by the scheduler.
    /// </summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Configuration for an object-store / file-watcher trigger. A new object landing under
/// the watched prefix fires the workflow. The baseline implementation is poll-based; the
/// shape is designed so an S3/event push can replace the poll later without changing the
/// trigger contract.
/// </summary>
public sealed record ObjectStoreTriggerConfig
{
    /// <summary>
    /// Logical object-store identifier resolved by the probe registry (e.g. a configured
    /// bucket/container/local-root binding). Required.
    /// </summary>
    public required string StoreId { get; init; }

    /// <summary>
    /// Key prefix under <see cref="StoreId"/> that is watched for new objects. Empty watches
    /// the whole store.
    /// </summary>
    public string Prefix { get; init; } = string.Empty;

    /// <summary>
    /// Optional minimum interval between polls of the store. Null lets the scheduler poll on
    /// every tick.
    /// </summary>
    public TimeSpan? PollInterval { get; init; }
}

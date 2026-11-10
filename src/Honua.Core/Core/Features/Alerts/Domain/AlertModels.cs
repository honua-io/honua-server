// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Alerts.Domain;

public enum AlertEdition
{
    Pro = 1,

    Enterprise = 2,
}

// A missing entitlement and an edition cap need different remedies, so the admin surface must not report them the same way.
public enum AlertEditionDenialReason
{
    None = 0,

    MissingEntitlement = 1,

    // An edition-cap denial must not describe the entitlement key as missing, or the operator is sent to buy a license they already hold.
    EditionCap = 2,
}

public enum AlertTriggerType
{
    Enter = 1,

    Exit = 2,

    Dwell = 3,

    Threshold = 4,
}

public enum AlertSeverity
{
    Info = 1,

    Warning = 2,

    Critical = 3,
}

public enum AlertChannelType
{
    Webhook = 1,

    WebSocket = 2,

    Email = 3,

    Digest = 4,

    AwsSns = 5,

    AzureEventGrid = 6,

    Slack = 7,

    MicrosoftTeams = 8,

    AwsSqs = 9,

    AzureEventHub = 10,
}

public enum AlertIncidentStatus
{
    Started = 1,

    Ongoing = 2,

    Ended = 3,
}

public enum AlertDispatchStatus
{
    Pending = 0,

    Processing = 1,

    Delivered = 2,

    Failed = 3,

    DeadLetter = 4,
}

public enum AlertChangeOperation
{
    Insert = 1,

    Update = 2,

    Delete = 3,
}

public sealed record AlertZoneDefinition
{
    public required long ZoneId { get; init; }

    public required string ServiceId { get; init; }

    public required string ZoneName { get; init; }

    public byte[]? Geometry { get; init; }

    public int? GeometrySrid { get; init; }

    public required bool IsActive { get; init; }

    public ImmutableDictionary<string, string?> Metadata { get; init; } = ImmutableDictionary<string, string?>.Empty;
}

public sealed record AlertRuleDefinition
{
    public required long RuleId { get; init; }

    public required string ServiceId { get; init; }

    public required int LayerId { get; init; }

    public long? ZoneId { get; init; }

    public required string RuleName { get; init; }

    public required AlertTriggerType TriggerType { get; init; }

    public string ConditionsJson { get; init; } = "{}";

    public int CooldownSeconds { get; init; }

    public required AlertSeverity Severity { get; init; }

    public required AlertEdition EditionRequired { get; init; }

    public ImmutableArray<AlertChannelType> Channels { get; init; } = ImmutableArray<AlertChannelType>.Empty;

    public required bool IsActive { get; init; }
}

public sealed record AlertRuleDeliveryHealth
{
    public required AlertChannelType ChannelType { get; init; }

    public required int PendingCount { get; init; }

    public required int ProcessingCount { get; init; }

    public required int DeliveredCount { get; init; }

    public required int FailedCount { get; init; }

    public required int DeadLetterCount { get; init; }

    public DateTimeOffset? LastAttemptAt { get; init; }

    public DateTimeOffset? LastDeliveredAt { get; init; }

    public string? LastError { get; init; }
}

public sealed record AlertRuleHealthSnapshot
{
    public required long RuleId { get; init; }

    public DateTimeOffset? LastEvaluatedAt { get; init; }

    public DateTimeOffset? LastTriggeredAt { get; init; }

    public required int ActiveIncidentCount { get; init; }

    public required int RecentTriggerCount { get; init; }

    public required int CoolingDownFeatureCount { get; init; }

    public DateTimeOffset? NextCooldownExpiresAt { get; init; }

    public required int DeliveryFailureCount { get; init; }

    public required int DeadLetterCount { get; init; }

    public ImmutableArray<long> LinkedEventIds { get; init; } = ImmutableArray<long>.Empty;

    public ImmutableArray<AlertRuleDeliveryHealth> DeliveryChannels { get; init; } = ImmutableArray<AlertRuleDeliveryHealth>.Empty;
}

public readonly record struct AlertChange
{
    public required long Generation { get; init; }

    public required int LayerId { get; init; }

    public required long ObjectId { get; init; }

    public required AlertChangeOperation Operation { get; init; }

    public required DateTimeOffset ChangedAt { get; init; }

    public string? SourceEventId { get; init; }

    public string? OperationInstanceId { get; init; }

    public string? CorrelationId { get; init; }

    public string? AuditId { get; init; }

    public string? ProposalId { get; init; }

    public Feature? CommittedFeature { get; init; }

    public bool HasCommittedImage { get; init; }
}

public readonly record struct AlertRuleLookupKey(string? ServiceId, int LayerId);

public readonly record struct AlertStateLookupKey(long RuleId, int LayerId, long ObjectId);

public sealed record AlertStateSnapshot
{
    public required long RuleId { get; init; }

    public required int LayerId { get; init; }

    public required long ObjectId { get; init; }

    public required bool Inside { get; init; }

    public DateTimeOffset? EnteredAt { get; init; }

    public DateTimeOffset? LastAlertAt { get; init; }

    public required long LastGeneration { get; init; }

    public string ThresholdStateJson { get; init; } = "{}";
}

public static class AlertEventSources
{
    public const string GisRule = "gis";

    public const string Ops = "ops";
}

public sealed record AlertDispatchBacklog
{
    public required long PendingCount { get; init; }

    public required long DeadLetteredCount { get; init; }

    public long RetryingCount { get; init; }

    public DateTimeOffset? OldestItemAt { get; init; }

    public IReadOnlyList<AlertDispatchChannelBacklog> Channels { get; init; } = [];
}

// This health model carries only bounded identifiers, counts, and timestamps, and never destinations, payloads, errors, credentials, or secret references.
public sealed record AlertDispatchChannelBacklog
{
    public required AlertChannelType ChannelType { get; init; }

    public required bool IsPaused { get; init; }

    public required long PendingCount { get; init; }

    public required long RetryingCount { get; init; }

    public required long DeadLetteredCount { get; init; }

    public required DateTimeOffset OldestItemAt { get; init; }
}

public sealed record AlertEventEnvelope
{
    public required string DedupeKey { get; init; }

    public required long RuleId { get; init; }

    public long? ZoneId { get; init; }

    public required string ServiceId { get; init; }

    public required int LayerId { get; init; }

    public required long ObjectId { get; init; }

    public required AlertTriggerType TriggerType { get; init; }

    public required long Generation { get; init; }

    public required AlertSeverity Severity { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public string PayloadJson { get; init; } = "{}";

    public AlertIncidentStatus IncidentStatus { get; init; } = AlertIncidentStatus.Started;

    public long IncidentDurationMs { get; init; }

    public string? Source { get; init; }

    public string? SourceEventId { get; init; }

    public string? JobId { get; init; }

    public string? OperationInstanceId { get; init; }

    public string? CorrelationId { get; init; }

    public string? AuditId { get; init; }

    public string? ProposalId { get; init; }
}

public sealed record AlertDispatchItem
{
    public required Guid ClaimToken { get; init; }

    public required long DispatchId { get; init; }

    public required long EventId { get; init; }

    public required AlertChannelType ChannelType { get; init; }

    public string? Destination { get; init; }

    public required AlertDispatchStatus Status { get; init; }

    public required int Attempts { get; init; }

    public required int MaxAttempts { get; init; }

    public required DateTimeOffset NextAttemptAt { get; init; }
}

public sealed record AlertWorkerCheckpoint
{
    public required string WorkerName { get; init; }

    public required long LastGeneration { get; init; }

    public DateTimeOffset? LastDwellSweepAt { get; init; }
}

public sealed record AlertEvaluationResult
{
    public AlertStateSnapshot? UpdatedState { get; init; }

    public ImmutableArray<AlertEventEnvelope> Events { get; init; } = ImmutableArray<AlertEventEnvelope>.Empty;
}

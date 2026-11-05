// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Monitoring;

namespace Honua.Server.Features.Admin.Models;

internal sealed class ObservabilityAlertEventResponse
{
    public required long EventId { get; init; }

    public required long? RuleId { get; init; }

    public string? RuleName { get; init; }

    public long? ZoneId { get; init; }

    public required string ServiceId { get; init; }

    public required int LayerId { get; init; }

    public required long ObjectId { get; init; }

    public required string TriggerType { get; init; }

    public required string Severity { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required string IncidentStatus { get; init; }

    public required long IncidentDurationMs { get; init; }

    public required string LifecycleStatus { get; init; }

    public DateTimeOffset? AcknowledgedAt { get; init; }

    public string? AcknowledgedBy { get; init; }

    public DateTimeOffset? SuppressedUntil { get; init; }

    public DateTimeOffset? ResolvedAt { get; init; }

    public string? ResolvedBy { get; init; }

    public required string ResourceRef { get; init; }
}

internal sealed class ObservabilityAlertEventPageResponse
{
    public EvidencePosture? EvidencePosture { get; init; }

    public required IReadOnlyList<ObservabilityAlertEventResponse> Items { get; init; }

    public string? NextCursor { get; init; }
}

internal sealed class ObservabilityAlertAcknowledgeRequest
{
    public string? Note { get; init; }
}

internal sealed class ObservabilityAlertSuppressRequest
{
    public required DateTimeOffset SuppressUntil { get; init; }

    public string? Note { get; init; }
}

internal sealed class ObservabilityAlertResolveRequest
{
    public string? Note { get; init; }
}

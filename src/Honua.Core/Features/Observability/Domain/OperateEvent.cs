// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Observability.Domain;

public enum OperateEventKind
{
    Alert = 1,

    Audit = 2,

    Job = 3,

    Release = 4,

    SyncConflict = 5,

    TemporalOp = 6,

    Log = 7,
}

public enum OperateEventSeverity
{
    Info = 0,

    Notice = 1,

    Warning = 2,

    Error = 3,

    Critical = 4,
}

public sealed record OperateProviderLink
{
    public required string Provider { get; init; }

    public required string Label { get; init; }

    public required string Url { get; init; }
}

public sealed record OperateEvent
{
    public required string EventId { get; init; }

    public required OperateEventKind Kind { get; init; }

    public required OperateEventSeverity Severity { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required string Title { get; init; }

    public string? Summary { get; init; }

    public string? ServiceId { get; init; }

    public int? LayerId { get; init; }

    public long? ObjectId { get; init; }

    public string? Actor { get; init; }

    public string? CorrelationId { get; init; }

    public string? TraceId { get; init; }

    public string? RequestId { get; init; }

    public string? OperationId { get; init; }

    public string? ReleaseId { get; init; }

    public string? ReplicaId { get; init; }

    public string? ChangeSetId { get; init; }

    public string? ResourceRef { get; init; }

    public IReadOnlyList<OperateProviderLink>? ProviderLinks { get; init; }

    public string? DetailsJson { get; init; }
}

public sealed record OperateEventFilter
{
    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    public string? ServiceId { get; init; }

    public int? LayerId { get; init; }

    public IReadOnlyList<OperateEventKind>? Kinds { get; init; }

    public OperateEventSeverity? MinimumSeverity { get; init; }

    public string? CorrelationId { get; init; }

    public string? TraceId { get; init; }

    public string? RequestId { get; init; }

    public string? OperationId { get; init; }

    public string? ReleaseId { get; init; }

    public string? ChangeSetId { get; init; }

    public string? Actor { get; init; }

    public string? ResourceRef { get; init; }

    public int PageSize { get; init; } = 50;
}

public sealed record OperateEventPage
{
    public required IReadOnlyList<OperateEvent> Items { get; init; }

    public bool PartialResult { get; init; }

    // A truncated page is not a complete view of the requested window.
    public bool Truncated { get; init; }

    public IReadOnlyDictionary<OperateEventKind, string>? SourceErrors { get; init; }
}

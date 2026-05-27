// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Wire shape for a normalized operate event surfaced through the unified feed (#1168).
/// </summary>
internal sealed class OperateEventResponse
{
    /// <summary>Source-prefixed event identifier (e.g. "alert:42").</summary>
    public required string EventId { get; init; }

    /// <summary>Event category (lower-case: alert/audit/job/release/syncConflict/temporalOp/log).</summary>
    public required string Kind { get; init; }

    /// <summary>Normalized severity (lower-case: info/notice/warning/error/critical).</summary>
    public required string Severity { get; init; }

    /// <summary>Wall-clock time when the event occurred.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Short human-readable title.</summary>
    public required string Title { get; init; }

    /// <summary>Optional one-line summary.</summary>
    public string? Summary { get; init; }

    /// <summary>Service identifier when scoped to a service.</summary>
    public string? ServiceId { get; init; }

    /// <summary>Layer identifier when scoped to a layer.</summary>
    public int? LayerId { get; init; }

    /// <summary>Feature object identifier when scoped to a feature.</summary>
    public long? ObjectId { get; init; }

    /// <summary>Actor that produced the event when known.</summary>
    public string? Actor { get; init; }

    /// <summary>Cross-cutting correlation id.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Distributed trace id when known.</summary>
    public string? TraceId { get; init; }

    /// <summary>Request id when known.</summary>
    public string? RequestId { get; init; }

    /// <summary>Operation/job id when known.</summary>
    public string? OperationId { get; init; }

    /// <summary>Release id when known.</summary>
    public string? ReleaseId { get; init; }

    /// <summary>Replica id when known.</summary>
    public string? ReplicaId { get; init; }

    /// <summary>Change-set id when known.</summary>
    public string? ChangeSetId { get; init; }

    /// <summary>Typed resource reference for deep linking (e.g. "alert/42").</summary>
    public string? ResourceRef { get; init; }

    /// <summary>Deep links to upstream observability providers.</summary>
    public IReadOnlyList<OperateProviderLinkResponse>? ProviderLinks { get; init; }

    /// <summary>Source-specific structured payload as JSON text.</summary>
    public string? DetailsJson { get; init; }
}

/// <summary>
/// Wire shape for a provider deep link.
/// </summary>
internal sealed class OperateProviderLinkResponse
{
    /// <summary>Provider category (e.g. "grafana", "loki", "ci").</summary>
    public required string Provider { get; init; }

    /// <summary>Human-readable label.</summary>
    public required string Label { get; init; }

    /// <summary>Absolute or relative URL the Console can open.</summary>
    public required string Url { get; init; }
}

/// <summary>
/// Page of operate events.
/// </summary>
internal sealed class OperateEventPageResponse
{
    /// <summary>Items returned in this page (newest first).</summary>
    public required IReadOnlyList<OperateEventResponse> Items { get; init; }

    /// <summary>True when at least one source produced a partial result.</summary>
    public bool PartialResult { get; init; }

    /// <summary>Per-source diagnostic messages keyed by lower-case kind.</summary>
    public IReadOnlyDictionary<string, string>? SourceErrors { get; init; }
}

/// <summary>
/// Log entry exposed via the Operate logs endpoint.
/// </summary>
internal sealed class OperateLogEntryResponse
{
    /// <summary>When the entry was captured.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Severity level (always "error" for the current buffer).</summary>
    public required string Level { get; init; }

    /// <summary>Request path that produced the error.</summary>
    public string? Path { get; init; }

    /// <summary>HTTP status code.</summary>
    public required int StatusCode { get; init; }

    /// <summary>Correlation id captured at request time.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>Sanitized error message.</summary>
    public required string Message { get; init; }
}

/// <summary>
/// Response wrapping the recent log buffer with instance metadata.
/// </summary>
internal sealed class OperateLogPageResponse
{
    /// <summary>Identifier of the replica that produced the response.</summary>
    public required string InstanceId { get; init; }

    /// <summary>Maximum number of log entries retained.</summary>
    public required int Capacity { get; init; }

    /// <summary>Items returned in this page (newest first).</summary>
    public required IReadOnlyList<OperateLogEntryResponse> Items { get; init; }
}

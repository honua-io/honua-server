// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Spec.Domain;

/// <summary>
/// Per-node status surfaced in the apply event stream.
/// </summary>
public enum SpecApplyEventKind
{
    /// <summary>Node has been admitted to the orchestration queue.</summary>
    Queued,

    /// <summary>Node is actively executing via the compute backend.</summary>
    Running,

    /// <summary>Node output was satisfied from the content-hash cache.</summary>
    Cached,

    /// <summary>Node completed successfully; output is addressable via cache.</summary>
    Succeeded,

    /// <summary>Node failed; downstream nodes are skipped.</summary>
    Failed,

    /// <summary>Node was skipped because an upstream node failed.</summary>
    Skipped,

    /// <summary>Warning emitted during node execution (non-terminal).</summary>
    Warning,

    /// <summary>Apply-level event — apply has started; no node yet.</summary>
    ApplyStarted,

    /// <summary>Apply reached a terminal state; includes aggregate summary.</summary>
    ApplyCompleted,

    /// <summary>Apply was cancelled cooperatively; includes per-branch status.</summary>
    ApplyCancelled
}

/// <summary>
/// A single event on the apply stream. Flows over SSE (REST) and
/// gRPC server-streaming. Shape is identical on both transports.
/// </summary>
public sealed record SpecApplyEvent
{
    /// <summary>
    /// Monotonically increasing sequence number within an apply run, starting
    /// at 1. Clients may use this to detect drops.
    /// </summary>
    public required long Sequence { get; init; }

    /// <summary>
    /// Event kind.
    /// </summary>
    public required SpecApplyEventKind Kind { get; init; }

    /// <summary>
    /// Apply token this event belongs to. Opaque GUID; used to correlate
    /// events with the apply and to cancel the run.
    /// </summary>
    public required string ApplyToken { get; init; }

    /// <summary>
    /// Node identifier the event pertains to; null for apply-level events.
    /// </summary>
    public string? NodeId { get; init; }

    /// <summary>
    /// Content hash of the node output (present from <see cref="SpecApplyEventKind.Cached"/>
    /// and <see cref="SpecApplyEventKind.Succeeded"/>).
    /// </summary>
    public string? ContentHash { get; init; }

    /// <summary>
    /// UTC timestamp the event was produced.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Structured diagnostic (used for warnings and failures).
    /// </summary>
    public SpecWarning? Diagnostic { get; init; }

    /// <summary>
    /// Actual cost measured during execution; populated on
    /// <see cref="SpecApplyEventKind.Succeeded"/> and
    /// <see cref="SpecApplyEventKind.Cached"/>.
    /// </summary>
    public SpecCostActual? ActualCost { get; init; }

    /// <summary>
    /// Apply summary populated on <see cref="SpecApplyEventKind.ApplyCompleted"/>
    /// and <see cref="SpecApplyEventKind.ApplyCancelled"/>.
    /// </summary>
    public SpecApplySummary? Summary { get; init; }
}

/// <summary>
/// Actual (post-execution) cost for a single node.
/// </summary>
public sealed record SpecCostActual
{
    /// <summary>Actual output row count, if the compute backend reports one.</summary>
    public long? Rows { get; init; }

    /// <summary>Actual output bytes.</summary>
    public long? Bytes { get; init; }

    /// <summary>Measured duration in milliseconds.</summary>
    public required double DurationMs { get; init; }
}

/// <summary>
/// Aggregate summary for an apply run. Attached to the terminal event.
/// </summary>
public sealed record SpecApplySummary
{
    /// <summary>Total nodes in the DAG.</summary>
    public required int TotalNodes { get; init; }

    /// <summary>Nodes satisfied from the content-hash cache.</summary>
    public required int CachedNodes { get; init; }

    /// <summary>Nodes that invoked the compute backend and succeeded.</summary>
    public required int RanNodes { get; init; }

    /// <summary>Nodes that failed.</summary>
    public required int FailedNodes { get; init; }

    /// <summary>Nodes skipped due to upstream failure or cancellation.</summary>
    public required int SkippedNodes { get; init; }

    /// <summary>Wall-clock duration of the apply run in milliseconds.</summary>
    public required double TotalDurationMs { get; init; }

    /// <summary>Whether the apply run was cancelled before completing.</summary>
    public required bool Cancelled { get; init; }
}

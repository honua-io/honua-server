// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

/// <summary>A status change persisted atomically by the authoritative job store.</summary>
public sealed record ExecutionJobStatusTransition
{
    /// <summary>Exact job identity whose record accepted this change.</summary>
    public required string JobId { get; init; }
    /// <summary>Actual store version accepting the change; progress-only writes may leave gaps.</summary>
    public required long Version { get; init; }
    /// <summary>Execution attempt retained on that accepted job version.</summary>
    public required int AttemptCount { get; init; }
    /// <summary>Authoritative Redis clock milliseconds captured in the atomic state write.</summary>
    public required long OccurredAtUnixMilliseconds { get; init; }
    /// <summary>UTC projection of the atomic storage timestamp.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTimeOffset OccurredAt => DateTimeOffset.FromUnixTimeMilliseconds(OccurredAtUnixMilliseconds);
    /// <summary>Actual prior stored status; absent only on initial creation.</summary>
    public ExecutionJobStatus? FromStatus { get; init; }
    /// <summary>Actual new stored status.</summary>
    public required ExecutionJobStatus ToStatus { get; init; }
}

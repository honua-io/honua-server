// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Share.Domain;

/// <summary>
/// A persisted, append-mostly record of an individual scheduled Share export run.
/// </summary>
public sealed record ShareExportRun
{
    /// <summary>Stable run identifier.</summary>
    public required string RunId { get; init; }

    /// <summary>Identifier of the export definition this run belongs to.</summary>
    public required string ExportId { get; init; }

    /// <summary>What initiated the run.</summary>
    public required ShareExportTriggerKind TriggerKind { get; init; }

    /// <summary>Lifecycle state of the run.</summary>
    public required ShareExportRunStatus Status { get; init; }

    /// <summary>
    /// The backing <c>ExecutionJob.OperationId</c> when the run is backed by the server job
    /// runner, enabling Console to deep-link to <c>/operate/jobs/{jobRunId}</c>. Null when no
    /// job runner was invoked (for example an unsupported destination).
    /// </summary>
    public string? JobRunId { get; init; }

    /// <summary>Time the run was triggered.</summary>
    public required DateTimeOffset TriggeredAt { get; init; }

    /// <summary>Time the run started executing, when known.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>Time the run reached a terminal state, when known.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Human-readable description of the destination the run targeted.</summary>
    public string? TargetSummary { get; init; }

    /// <summary>References to artifacts produced by the run.</summary>
    public IReadOnlyList<string> ResultArtifacts { get; init; } = Array.Empty<string>();

    /// <summary>Safe, store-neutral error message for failed runs.</summary>
    public string? LastError { get; init; }
}

/// <summary>
/// A page of export runs ordered newest first.
/// </summary>
public sealed record ShareExportRunPage
{
    /// <summary>Runs in this page.</summary>
    public required IReadOnlyList<ShareExportRun> Items { get; init; }

    /// <summary>Opaque continuation cursor, or null when no further pages exist.</summary>
    public string? NextCursor { get; init; }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.WorkflowPackages.Domain;

/// <summary>
/// Structural validation result for a workflow package graph or publication target.
/// </summary>
public sealed record WorkflowPackageValidationResult
{
    /// <summary>
    /// Whether the graph or publication target can be used.
    /// </summary>
    public required bool IsValid { get; init; }

    /// <summary>
    /// Validation failures that block use.
    /// </summary>
    public IReadOnlyList<WorkflowPackageValidationFailure> Failures { get; init; } = [];

    /// <summary>
    /// Non-fatal validation warnings.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Package hash evaluated during validation.
    /// </summary>
    public string? PackageHash { get; init; }

    /// <summary>
    /// Creates a successful validation result.
    /// </summary>
    public static WorkflowPackageValidationResult Success(
        string? packageHash = null,
        IReadOnlyList<string>? warnings = null)
        => new()
        {
            IsValid = true,
            PackageHash = packageHash,
            Warnings = warnings ?? []
        };

    /// <summary>
    /// Creates a failed validation result.
    /// </summary>
    public static WorkflowPackageValidationResult Failed(
        IReadOnlyList<WorkflowPackageValidationFailure> failures,
        string? packageHash = null,
        IReadOnlyList<string>? warnings = null)
        => new()
        {
            IsValid = false,
            Failures = failures,
            PackageHash = packageHash,
            Warnings = warnings ?? []
        };
}

/// <summary>
/// Single validation failure for a workflow package graph or publication target.
/// </summary>
public sealed record WorkflowPackageValidationFailure
{
    /// <summary>
    /// Machine-readable validation code.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Human-readable validation message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Optional JSON-style field path.
    /// </summary>
    public string? FieldPath { get; init; }
}

/// <summary>
/// Bounded dry-run result for a workflow package version.
/// </summary>
public sealed record WorkflowDryRunResult
{
    /// <summary>
    /// Validation result used by the dry run.
    /// </summary>
    public required WorkflowPackageValidationResult Validation { get; init; }

    /// <summary>
    /// Estimated duration in seconds.
    /// </summary>
    public int EstimatedDurationSeconds { get; init; }

    /// <summary>
    /// Estimated relative cost weight.
    /// </summary>
    public double EstimatedCostWeight { get; init; }

    /// <summary>
    /// Output schemas expected from the package.
    /// </summary>
    public IReadOnlyList<WorkflowNodePortSchema> OutputSchemas { get; init; } = [];

    /// <summary>
    /// Dry-run log entries.
    /// </summary>
    public IReadOnlyList<WorkflowDryRunLogEntry> Logs { get; init; } = [];

    /// <summary>
    /// Artifact previews produced by the dry run.
    /// </summary>
    public IReadOnlyList<WorkflowDryRunArtifact> Artifacts { get; init; } = [];

    /// <summary>
    /// Whether sample data was truncated.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>
    /// Package content hash.
    /// </summary>
    public required string PackageHash { get; init; }
}

/// <summary>
/// Log entry emitted by workflow package dry runs.
/// </summary>
public sealed record WorkflowDryRunLogEntry
{
    /// <summary>
    /// UTC timestamp.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Log level.
    /// </summary>
    public required string Level { get; init; }

    /// <summary>
    /// Log message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Optional node identifier associated with the log entry.
    /// </summary>
    public string? NodeId { get; init; }
}

/// <summary>
/// Artifact preview emitted by a dry run.
/// </summary>
public sealed record WorkflowDryRunArtifact
{
    /// <summary>
    /// Preview artifact identifier.
    /// </summary>
    public required string ArtifactId { get; init; }

    /// <summary>
    /// Artifact kind.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Human-readable label.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Output schema associated with the preview.
    /// </summary>
    public WorkflowSchemaDefinition? Schema { get; init; }
}

/// <summary>
/// Result of starting a workflow package publication run.
/// </summary>
public sealed record WorkflowPublicationRunResult
{
    /// <summary>
    /// Publication identifier.
    /// </summary>
    public required string PublicationId { get; init; }

    /// <summary>
    /// Package identifier.
    /// </summary>
    public required string PackageId { get; init; }

    /// <summary>
    /// Package version.
    /// </summary>
    public required int PackageVersion { get; init; }

    /// <summary>
    /// Publication target.
    /// </summary>
    public required WorkflowPublicationTarget Target { get; init; }

    /// <summary>
    /// Execution job identifier when the target created a job.
    /// </summary>
    public string? JobId { get; init; }

    /// <summary>
    /// Workflow run identifier when the target created a workflow run.
    /// </summary>
    public string? WorkflowRunId { get; init; }

    /// <summary>
    /// Provenance metadata stamped onto the created record.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Provenance { get; init; }
}

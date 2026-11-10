// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.WorkflowPackages.Domain;

public sealed record WorkflowPackageValidationResult
{
    public required bool IsValid { get; init; }

    public IReadOnlyList<WorkflowPackageValidationFailure> Failures { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string? PackageHash { get; init; }

    public static WorkflowPackageValidationResult Success(
        string? packageHash = null,
        IReadOnlyList<string>? warnings = null)
        => new()
        {
            IsValid = true,
            PackageHash = packageHash,
            Warnings = warnings ?? []
        };

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

public sealed record WorkflowPackageValidationFailure
{
    public required string Code { get; init; }

    public required string Message { get; init; }

    public string? FieldPath { get; init; }
}

public sealed record WorkflowDryRunResult
{
    public required WorkflowPackageValidationResult Validation { get; init; }

    public int EstimatedDurationSeconds { get; init; }

    public double EstimatedCostWeight { get; init; }

    public IReadOnlyList<WorkflowNodePortSchema> OutputSchemas { get; init; } = [];

    public IReadOnlyList<WorkflowDryRunLogEntry> Logs { get; init; } = [];

    public IReadOnlyList<WorkflowDryRunArtifact> Artifacts { get; init; } = [];

    public bool Truncated { get; init; }

    public required string PackageHash { get; init; }
}

public sealed record WorkflowDryRunLogEntry
{
    public required DateTimeOffset Timestamp { get; init; }

    public required string Level { get; init; }

    public required string Message { get; init; }

    public string? NodeId { get; init; }
}

public sealed record WorkflowDryRunArtifact
{
    public required string ArtifactId { get; init; }

    public required string Kind { get; init; }

    public required string Label { get; init; }

    public WorkflowSchemaDefinition? Schema { get; init; }
}

public sealed record WorkflowPublicationRunResult
{
    public required string PublicationId { get; init; }

    public required string PackageId { get; init; }

    public required int PackageVersion { get; init; }

    public required WorkflowPublicationTarget Target { get; init; }

    public string? JobId { get; init; }

    public string? WorkflowRunId { get; init; }

    public required IReadOnlyDictionary<string, string> Provenance { get; init; }
}

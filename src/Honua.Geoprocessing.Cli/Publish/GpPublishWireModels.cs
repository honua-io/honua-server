// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Geoprocessing.Cli.Publish;

// These records mirror the Console REST wire contract for the workflow-package
// endpoints (src/Honua.Server/Features/WorkflowPackages/WorkflowPackageEndpointModels.cs).
// The server-side request records are `internal` to Honua.Server, so the devkit cannot
// reference them directly; we re-declare the exact same shape here and (de)serialize with
// the same camelCase + string-enum policy the server's source-generated JSON context uses.
// They carry the public Honua.Core domain types (WorkflowGraph, WorkflowSchedule, ...) so
// the graph payload itself is the canonical model — only the request envelope is mirrored.

/// <summary>
/// Mirror of the server's <c>SaveWorkflowPackageRequest</c> body for
/// <c>POST /api/v{n}/console/workflow-packages</c>.
/// </summary>
internal sealed record SaveWorkflowPackageRequestDto
{
    /// <summary>Stable package identifier; null lets the server assign one.</summary>
    public string? PackageId { get; init; }

    /// <summary>Human-readable package name.</summary>
    public required string Name { get; init; }

    /// <summary>Optional package description.</summary>
    public string? Description { get; init; }

    /// <summary>Optional logical namespace for Console authoring.</summary>
    public string? Namespace { get; init; }

    /// <summary>The mutable draft graph to save.</summary>
    public required WorkflowGraph Graph { get; init; }

    /// <summary>Opaque metadata carried with the package draft.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Mirror of the server's <c>PublishWorkflowPackageRequest</c> body for
/// <c>POST /api/v{n}/console/workflow-packages/{id}/versions/{v}/publish</c>.
/// </summary>
internal sealed record PublishWorkflowPackageRequestDto
{
    /// <summary>Optional stable publication identifier.</summary>
    public string? PublicationId { get; init; }

    /// <summary>Publication target (Job, Schedule, or ProcessEndpoint).</summary>
    public required WorkflowPublicationTarget Target { get; init; }

    /// <summary>Process identifier when the target is a process endpoint.</summary>
    public string? ProcessId { get; init; }

    /// <summary>Schedule when the target is a schedule.</summary>
    public WorkflowSchedule? Schedule { get; init; }

    /// <summary>Whether the publication is enabled.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Mirror of the generic <c>ApiResponse&lt;T&gt;</c> envelope the Console endpoints return.
/// </summary>
/// <typeparam name="T">The response payload type.</typeparam>
internal sealed record ApiResponseDto<T>
{
    /// <summary>Whether the request succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>The response payload.</summary>
    public T? Data { get; init; }

    /// <summary>Optional response message.</summary>
    public string? Message { get; init; }
}

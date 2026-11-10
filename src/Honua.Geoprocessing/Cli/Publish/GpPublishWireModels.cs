// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Geoprocessing.Cli.Publish;

// These records mirror the Console REST wire contract for the workflow-package
// endpoints (src/Honua.Server/Features/WorkflowPackages/WorkflowPackageEndpointModels.cs).
// The server-side request records are `internal` to Honua.Server, so the devkit cannot
// reference them directly; we re-declare the exact same shape here and (de)serialize with
// the same camelCase + string-enum policy the server's source-generated JSON context uses.
// They carry the public Honua.Core domain types (WorkflowGraph, WorkflowSchedule, ...) so
// the graph payload itself is the canonical model — only the request envelope is mirrored.

internal sealed record SaveWorkflowPackageRequestDto
{
    public string? PackageId { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public string? Namespace { get; init; }

    public required WorkflowGraph Graph { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

internal sealed record PublishWorkflowPackageRequestDto
{
    public string? PublicationId { get; init; }

    public required WorkflowPublicationTarget Target { get; init; }

    public string? ProcessId { get; init; }

    public WorkflowSchedule? Schedule { get; init; }

    public bool Enabled { get; init; } = true;
}

internal sealed record ApiResponseDto<T>
{
    public bool Success { get; init; }

    public T? Data { get; init; }

    public string? Message { get; init; }
}

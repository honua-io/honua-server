// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Server.Features.WorkflowPackages;

internal sealed record SaveWorkflowPackageRequest
{
    public string? PackageId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Namespace { get; init; }
    public required WorkflowGraph Graph { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

internal sealed record PublishWorkflowPackageRequest
{
    public string? PublicationId { get; init; }
    public required WorkflowPublicationTarget Target { get; init; }
    public string? ProcessId { get; init; }
    public WorkflowSchedule? Schedule { get; init; }
    public bool Enabled { get; init; } = true;
}

internal sealed record RunWorkflowPublicationRequest
{
    public string? IdempotencyKey { get; init; }
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
}

internal sealed record WorkflowPackageListResponse
{
    public required WorkflowPackage[] Items { get; init; }
}

internal sealed record WorkflowPackageVersionListResponse
{
    public required WorkflowPackageVersion[] Items { get; init; }
}

internal sealed record WorkflowPublicationListResponse
{
    public required WorkflowPublication[] Items { get; init; }
}

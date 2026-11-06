// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Core.Features.Operations.Domain;

public sealed record OperationPolicyMetadata
{
    public required OperationBlastRadiusClass BlastRadiusClass { get; init; }

    public required OperationSideEffectClass SideEffectClass { get; init; }

    public required OperationDeterminism Determinism { get; init; }

    public required bool SupportsDryRun { get; init; }
}

public sealed record OperationDescriptor : IOperationDescriptor
{
    public required string OperationId { get; init; }

    public required string ProviderId { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    public required string Category { get; init; }

    public IReadOnlyList<OperationParameterDescriptor> InputSchema { get; init; } = [];

    public IReadOnlyList<OperationParameterDescriptor> OutputSchema { get; init; } = [];

    public required OperationExecutionKind ExecutionKind { get; init; }

    public required OperationApprovalModel ApprovalModel { get; init; }

    public bool IsCompatibilityOnly { get; init; }

    public required OperationPolicyMetadata Policy { get; init; }

    public OperationGroundingMetadata? Grounding { get; init; }
}

public sealed record OperationParameterDescriptor
{
    public required string Name { get; init; }

    public required string Title { get; init; }

    public bool Required { get; init; }

    public required WorkflowSchemaDefinition Schema { get; init; }
}

public sealed record OperationGroundingMetadata
{
    public IReadOnlyList<string> Synonyms { get; init; } = [];
}

public sealed record OperationCatalogSnapshot
{
    public required string CatalogVersion { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }

    public IReadOnlyList<string> ProviderIds { get; init; } = [];

    public required IReadOnlyList<OperationDescriptor> Operations { get; init; }
}

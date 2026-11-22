// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
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

public enum OperationCompensationAvailability
{
    NotApplicable,

    Unavailable,

    Supported
}

public sealed record OperationCompensationMetadata(
    OperationCompensationAvailability Availability,
    string? Reason)
{
    public static OperationCompensationMetadata For(OperationSideEffectClass sideEffectClass) =>
        sideEffectClass == OperationSideEffectClass.ReadOnly
            ? new(OperationCompensationAvailability.NotApplicable, null)
            : new(OperationCompensationAvailability.Unavailable,
                "No compensation executor and durable recovery evidence are registered for this operation.");
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

    [JsonIgnore]
    public OperationCompensationMetadata? DeclaredCompensation { get; init; }

    public OperationCompensationMetadata Compensation =>
        DeclaredCompensation ?? OperationCompensationMetadata.For(Policy.SideEffectClass);

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

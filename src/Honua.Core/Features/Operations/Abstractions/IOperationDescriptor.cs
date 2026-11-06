// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Abstractions;

public interface IOperationDescriptor
{
    string OperationId { get; }

    string ProviderId { get; }

    string Title { get; }

    string Description { get; }

    string Category { get; }

    IReadOnlyList<OperationParameterDescriptor> InputSchema { get; }

    IReadOnlyList<OperationParameterDescriptor> OutputSchema { get; }

    OperationExecutionKind ExecutionKind { get; }

    OperationApprovalModel ApprovalModel { get; }

    // An internal compatibility descriptor must never be offered through public REST, MCP, CLI, or Studio discovery.
    bool IsCompatibilityOnly { get; }

    OperationPolicyMetadata Policy { get; }

    OperationGroundingMetadata? Grounding { get; }
}

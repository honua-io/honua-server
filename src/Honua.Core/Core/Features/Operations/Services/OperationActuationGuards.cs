// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;

namespace Honua.Core.Features.Operations.Services;

// Checks the dispatcher repeats against durable state just before it runs an operation.
public sealed record OperationActuationGuards
{
    public IOperationApprovalReplayVerifier? ApprovalReplayVerifier { get; init; }

    public IAgentAuthorityStore? AgentAuthorityStore { get; init; }
}

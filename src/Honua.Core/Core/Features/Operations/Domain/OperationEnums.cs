// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Operations.Domain;

public enum OperationExecutionKind
{
    Synchronous,

    Job,

    ProposalHandoff
}

public enum OperationApprovalModel
{
    None,

    OperatorGate,

    StudioPublishRequest,

    DevOpsDelegatedSession
}

public enum OperationBlastRadiusClass
{
    None,

    ResourceScope,

    ServiceScope,

    DeploymentScope
}

public enum OperationSideEffectClass
{
    ReadOnly,

    CreatesMetadata,

    MutatesMetadata,

    MutatesData,

    DestroysState
}

public enum OperationDeterminism
{
    Deterministic,

    RuntimeDynamic,

    AiAssisted
}

public enum OperationHandleStatus
{
    Accepted,

    Completed,

    Queued,

    Running,

    RequiresApproval,

    // No side effect occurred; the caller must dry-run and then commit.
    DryRunRequired,

    // A policy denial never reached the executor, so no side effect occurred.
    Denied,

    Rejected,

    // Cancellation before actuation has no side effect, and a retry is a new operation unless a domain idempotency key says otherwise.
    Cancelled,

    Failed,

    Indeterminate
}

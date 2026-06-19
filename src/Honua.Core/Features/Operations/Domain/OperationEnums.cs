// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Operations.Domain;

/// <summary>
/// How an operation is executed. The descriptor declares the kind so that callers
/// (the deterministic console UI today, the AI orchestrator later) know whether a
/// <see cref="Abstractions.IOperationExecutor.SubmitAsync"/> returns an inline result,
/// queues a durable job, or hands off a proposal for out-of-band execution.
/// </summary>
public enum OperationExecutionKind
{
    /// <summary>
    /// The operation completes inline within the request and returns its result directly.
    /// </summary>
    Synchronous,

    /// <summary>
    /// The operation is submitted to the durable job substrate and tracked via a job id.
    /// </summary>
    Job,

    /// <summary>
    /// The operation produces a proposal that is executed out-of-band (for example the
    /// DevOps agent's gitops/delegated-session flow). Submit returns a handle describing
    /// the handoff rather than executing the side effect.
    /// </summary>
    ProposalHandoff
}

/// <summary>
/// Human-gate model an operation flows through. Approval models are plural: publish
/// uses an operator gate at the HTTP layer, Studio publish-requests use a request lane,
/// and DevOps uses delegated sessions. The descriptor declares which one applies.
/// </summary>
public enum OperationApprovalModel
{
    /// <summary>
    /// No human gate; the operation runs subject only to the policy decision point.
    /// </summary>
    None,

    /// <summary>
    /// Guarded by the operator approval gate enforced at the HTTP layer
    /// (for example <c>LayerPublishingEndpoints</c>).
    /// </summary>
    OperatorGate,

    /// <summary>
    /// Routed through the Studio publish-request approval lane.
    /// </summary>
    StudioPublishRequest,

    /// <summary>
    /// Routed through the DevOps delegated-session approval flow.
    /// </summary>
    DevOpsDelegatedSession
}

/// <summary>
/// Blast-radius classification for an operation, used by the policy seam to reason about
/// how much state an invocation can affect.
/// </summary>
public enum OperationBlastRadiusClass
{
    /// <summary>
    /// Affects no durable state (read-only / diagnostic).
    /// </summary>
    None,

    /// <summary>
    /// Affects a single resource (one layer, one style, one item).
    /// </summary>
    ResourceScope,

    /// <summary>
    /// Affects a single service and its contained resources.
    /// </summary>
    ServiceScope,

    /// <summary>
    /// Affects tenant-wide or deployment-wide state.
    /// </summary>
    DeploymentScope
}

/// <summary>
/// Side-effect classification for an operation, used by the policy seam.
/// </summary>
public enum OperationSideEffectClass
{
    /// <summary>
    /// No side effects; read-only.
    /// </summary>
    ReadOnly,

    /// <summary>
    /// Creates new metadata/catalog state.
    /// </summary>
    CreatesMetadata,

    /// <summary>
    /// Mutates existing metadata/catalog state.
    /// </summary>
    MutatesMetadata,

    /// <summary>
    /// Mutates underlying data (features/rows), not just metadata.
    /// </summary>
    MutatesData,

    /// <summary>
    /// Deletes durable state.
    /// </summary>
    DestroysState
}

/// <summary>
/// Whether an operation's behavior is deterministic or AI-assisted. Deterministic mode
/// is "the same operations with AI off"; this flag lets the policy seam and audit treat
/// AI-assisted operations differently from deterministic ones.
/// </summary>
public enum OperationDeterminism
{
    /// <summary>
    /// Fully deterministic given its inputs.
    /// </summary>
    Deterministic,

    /// <summary>
    /// Uses AI assistance and may produce non-deterministic output.
    /// </summary>
    AiAssisted
}

/// <summary>
/// Lifecycle status of a submitted operation, carried by <see cref="OperationHandle"/>.
/// </summary>
public enum OperationHandleStatus
{
    /// <summary>
    /// The operation completed synchronously and its result is available on the handle.
    /// </summary>
    Completed,

    /// <summary>
    /// The operation was queued as a durable job; track progress via the job id.
    /// </summary>
    Queued,

    /// <summary>
    /// The operation is currently running.
    /// </summary>
    Running,

    /// <summary>
    /// The operation requires human approval before it can proceed; an approval lane is attached.
    /// </summary>
    RequiresApproval,

    /// <summary>
    /// The operation failed.
    /// </summary>
    Failed
}

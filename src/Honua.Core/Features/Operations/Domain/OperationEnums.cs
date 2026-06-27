// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Operations.Domain;

/// <summary>
/// Execution surface an operation routes through when it is invoked.
/// </summary>
/// <remarks>
/// Mirrors the "one surface, two callers" target architecture: the deterministic
/// console UI and the AI orchestrator invoke the same operation, but the operation
/// declares whether it runs inline or is handed to a Studio publish-request or
/// DevOps deploy-control surface.
/// </remarks>
public enum OperationExecutionLane
{
    /// <summary>
    /// The operation executes inline and returns its result directly.
    /// </summary>
    Synchronous,

    /// <summary>
    /// The operation is routed through the Studio publish-request surface.
    /// </summary>
    StudioPublishRequest,

    /// <summary>
    /// The operation is routed through the DevOps deploy-control surface.
    /// </summary>
    DevOpsDeployControl
}

/// <summary>
/// Human-approval lane an operation lands in before (or instead of) execution.
/// </summary>
/// <remarks>
/// The approval lane is metadata describing where a human gate would surface; the
/// approval engine itself is not built in this spike. <see cref="None"/> means the
/// operation executes directly today.
/// </remarks>
public enum OperationApprovalLane
{
    /// <summary>
    /// No human approval lane; the operation executes directly.
    /// </summary>
    None,

    /// <summary>
    /// The operation lands in the Studio publish-request approval lane.
    /// </summary>
    PublishRequest,

    /// <summary>
    /// The operation lands in the DevOps deploy-control approval lane.
    /// </summary>
    DeployControl
}

/// <summary>
/// Coarse classification of how widely an operation's side effects can propagate.
/// </summary>
/// <remarks>
/// This is the blast-radius signal a tier- and role-aware policy decision point uses
/// to decide whether to allow, require approval, force a dry run, or deny. The engine
/// is later; the classification is part of the contract now so it is never a retrofit.
/// </remarks>
public enum OperationBlastRadiusClass
{
    /// <summary>
    /// The operation has no externally observable side effects.
    /// </summary>
    None,

    /// <summary>
    /// Effects are scoped to a single layer or resource.
    /// </summary>
    ResourceScope,

    /// <summary>
    /// Effects are scoped to a single service and its layers.
    /// </summary>
    ServiceScope,

    /// <summary>
    /// Effects span an entire deployment or environment.
    /// </summary>
    DeploymentScope
}

/// <summary>
/// Classification of the kind of side effect an operation produces.
/// </summary>
public enum OperationSideEffectClass
{
    /// <summary>
    /// The operation only reads state.
    /// </summary>
    ReadOnly,

    /// <summary>
    /// The operation creates or mutates catalog/metadata records.
    /// </summary>
    CreatesMetadata,

    /// <summary>
    /// The operation mutates stored feature or tabular data.
    /// </summary>
    MutatesData,

    /// <summary>
    /// The operation changes deployment or runtime topology.
    /// </summary>
    MutatesDeployment
}

/// <summary>
/// Whether an operation's outcome is deterministic or produced with AI assistance.
/// </summary>
/// <remarks>
/// "Deterministic mode" is the same operation catalog with AI off; this flag lets a
/// policy gate distinguish AI-assisted invocations for guardrail-wary operators.
/// </remarks>
public enum OperationDeterminism
{
    /// <summary>
    /// The operation produces a deterministic result for a given input.
    /// </summary>
    Deterministic,

    /// <summary>
    /// The operation's result is produced with AI assistance.
    /// </summary>
    AiAssisted
}

/// <summary>
/// Lane a policy decision routes an invocation into.
/// </summary>
public enum OperationPolicyOutcome
{
    /// <summary>
    /// The invocation is permitted to execute.
    /// </summary>
    Allow,

    /// <summary>
    /// The invocation must be routed to a human approval lane before execution.
    /// </summary>
    RequireApproval,

    /// <summary>
    /// The invocation must be dry-run/previewed before a real execution is permitted.
    /// </summary>
    DryRunFirst,

    /// <summary>
    /// The invocation is denied.
    /// </summary>
    Deny
}

/// <summary>
/// Subscription tier a policy decision point evaluates against.
/// </summary>
/// <remarks>
/// Community is pass-through (bring-your-own-model, no policy gate); Pro/Enterprise
/// are policy-gated. Only <see cref="Community"/> behaviour ships in this spike.
/// </remarks>
public enum OperationTier
{
    /// <summary>
    /// Community tier: pass-through policy, no enforced guardrails.
    /// </summary>
    Community,

    /// <summary>
    /// Pro tier: policy-gated (approval lanes, dry-run-first, blast-radius limits).
    /// </summary>
    Pro,

    /// <summary>
    /// Enterprise tier: full policy plus RBAC and audit.
    /// </summary>
    Enterprise
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Guardrails.Domain;

/// <summary>
/// Mutating control-plane operation classes that are subject to the edition
/// guardrail ladder. Data-plane feature edits (applyEdits/WFS-T/OData CRUD)
/// are intentionally out of scope for v1 (#1690).
/// </summary>
public enum OperationClass
{
    /// <summary>
    /// Administrative configuration change (server settings, service/layer
    /// configuration, RBAC, identity providers, and similar admin mutations).
    /// </summary>
    AdminConfigChange = 0,

    /// <summary>
    /// Deploy or rollback of a target revision through the control plane.
    /// </summary>
    Deploy = 1,

    /// <summary>
    /// Metadata release package progression through deploy/evidence/rollback.
    /// </summary>
    MetadataRelease = 2,

    /// <summary>
    /// Seed or bootstrap operation that materializes catalog/sample state.
    /// </summary>
    Seed = 3,

    /// <summary>
    /// A destructive or sink/write geoprocessing plan submission whose approval
    /// requirement was decided upstream by the geoprocessing destructive-plan gate
    /// (<c>ProcessDestructiveClassifier</c>). Routed through the shared operation
    /// proposal/gateway surface so a gated plan is persisted as an
    /// <c>AwaitingApproval</c> proposal and resumed on approval, rather than
    /// dead-ending at submission (ADR-0064, #2814).
    /// </summary>
    Geoprocess = 4,
}

/// <summary>
/// Resolved guardrail tier for a mutating operation at the active edition.
/// </summary>
public enum GuardrailTier
{
    /// <summary>
    /// The operation may execute directly through the existing pipeline,
    /// subject to RBAC and entitlement gates.
    /// </summary>
    DirectExecute = 0,

    /// <summary>
    /// The operation must be persisted as a durable proposal and resolved by a
    /// human approver before it executes.
    /// </summary>
    RequiresApproval = 1,

    /// <summary>
    /// The operation is not permitted at the active edition and must be
    /// rejected with an entitlement/approval problem.
    /// </summary>
    Blocked = 2,
}

/// <summary>
/// Outcome of a guardrail ladder evaluation, carrying the resolved tier and the
/// inputs that produced it for audit and operator-facing messaging.
/// </summary>
/// <param name="Tier">Resolved guardrail tier.</param>
/// <param name="OperationClass">Operation class that was evaluated.</param>
/// <param name="Edition">Active platform edition at evaluation time.</param>
/// <param name="Source">Human-readable reason describing why the tier was chosen.</param>
public sealed record GuardrailDecision(
    GuardrailTier Tier,
    OperationClass OperationClass,
    Honua.Core.Features.Licensing.Domain.HonuaEdition Edition,
    string Source);

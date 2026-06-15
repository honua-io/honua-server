// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Licensing.Domain;

namespace Honua.Core.Features.Guardrails.Abstractions;

/// <summary>
/// Resolves the edition guardrail tier (<see cref="GuardrailTier.DirectExecute"/>,
/// <see cref="GuardrailTier.RequiresApproval"/>, or <see cref="GuardrailTier.Blocked"/>)
/// for a mutating operation class at a given platform edition.
/// </summary>
/// <remarks>
/// The ladder is the single authority that answers, for <em>this operation class</em>
/// at <em>this edition</em>, whether to execute directly, route to approval, or block.
/// It is the contract every approval surface keys off (#1691). Community and Pro
/// execute directly (subject to RBAC + entitlement); Enterprise requires approval
/// for in-scope mutating operation classes.
/// </remarks>
public interface IGuardrailLadder
{
    /// <summary>
    /// Resolves the guardrail tier for the supplied operation class at the
    /// supplied edition, applying operator overrides where configured.
    /// </summary>
    /// <param name="operationClass">Mutating operation class being evaluated.</param>
    /// <param name="edition">Active platform edition.</param>
    /// <returns>The resolved guardrail decision.</returns>
    GuardrailDecision Resolve(OperationClass operationClass, HonuaEdition edition);

    /// <summary>
    /// Resolves the guardrail tier for the supplied operation class using the
    /// active license edition.
    /// </summary>
    /// <param name="operationClass">Mutating operation class being evaluated.</param>
    /// <returns>The resolved guardrail decision.</returns>
    GuardrailDecision Resolve(OperationClass operationClass);
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Guardrails.Domain;

namespace Honua.Core.Features.Guardrails.Abstractions;

/// <summary>
/// Supplies the per-action guardrail tier for a control-plane ops action
/// discriminator. The concrete ops-action registry implements this so the
/// guardrail ladder can resolve a tier per action (not just per
/// <see cref="OperationClass"/>). An action the catalog does not recognize has no
/// declared tier and the ladder fails it closed to <see cref="GuardrailTier.Blocked"/>.
/// </summary>
public interface IOpsActionGuardrailCatalog
{
    /// <summary>
    /// Attempts to resolve the declared guardrail tier for the supplied action.
    /// </summary>
    /// <param name="action">Action discriminator (for example <c>alerts.redrive_dead_letters</c>).</param>
    /// <param name="tier">Declared guardrail tier when the action is known.</param>
    /// <returns>True when the action is registered; otherwise false.</returns>
    bool TryGetTier(string action, out GuardrailTier tier);
}

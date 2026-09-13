// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Guardrails.Domain;

/// <summary>
/// Guardrail actions declared by the platform itself rather than by the control-plane
/// ops-action catalog. Their tier is a floor applied on top of the edition and operator
/// policy, exactly like a catalogued action: it can tighten a guardrail but never loosen it.
/// </summary>
public static class BuiltInGuardrailActions
{
    /// <summary>
    /// A Studio publication proposal submitted through an agent tool (honua-server#3429,
    /// #3304). The tool promises that publication waits for a separate authorized principal,
    /// so the proposal requires approval on every edition, while ordinary Studio draft
    /// composition under the same operation class keeps the edition tier.
    /// </summary>
    public const string StudioPublicationProposal = "studio.publication_proposal";

    /// <summary>Resolves the declared tier floor of a built-in action.</summary>
    /// <param name="action">Action discriminator.</param>
    /// <param name="tier">The declared tier when the action is built in.</param>
    /// <returns><see langword="true"/> when <paramref name="action"/> is a built-in action.</returns>
    public static bool TryGetTier(string action, out GuardrailTier tier)
    {
        if (string.Equals(action, StudioPublicationProposal, StringComparison.Ordinal))
        {
            tier = GuardrailTier.RequiresApproval;
            return true;
        }

        tier = GuardrailTier.Blocked;
        return false;
    }
}

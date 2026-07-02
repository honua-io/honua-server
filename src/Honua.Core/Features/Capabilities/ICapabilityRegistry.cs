// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Capabilities;

/// <summary>
/// The single source of truth for the platform's capability roster (ADR-0058,
/// Decision B). Both the <c>/mcp</c> surface and Studio AI bind to it, and every
/// other surface becomes a projection derived from it. B1 populates it to mirror
/// the live roster; B2 (#2334) composes <c>/mcp</c> from it and B3 (#2335) layers
/// the #1186 per-environment resolver onto it.
/// </summary>
public interface ICapabilityRegistry
{
    /// <summary>All registered capability descriptors, in a stable order.</summary>
    IReadOnlyList<CapabilityDescriptor> All { get; }

    /// <summary>
    /// Finds a descriptor by its <see cref="CapabilityDescriptor.Id"/>, or returns
    /// <c>null</c> when no capability with that id is registered.
    /// </summary>
    /// <param name="id">The stable capability identifier.</param>
    CapabilityDescriptor? Find(string id);

    /// <summary>
    /// Resolves whether a capability is enabled for the given context. This is the
    /// single evaluation entry point the gate resolver (#2339 / T2) and downstream
    /// gates call.
    /// </summary>
    /// <remarks>
    /// In B1 this is a pass-through: every registered capability resolves
    /// <c>Enabled = true</c> (no capability is disabled in this PR) and an unknown
    /// id resolves <c>Enabled = false</c>. The config-driven experimental gating and
    /// its reason codes land in #2339 (T2), which replaces the pass-through with
    /// <see cref="CapabilityGateContext"/>-driven precedence.
    /// </remarks>
    /// <param name="id">The stable capability identifier.</param>
    /// <param name="context">The edition/environment/config inputs to evaluate against.</param>
    CapabilityResolution Resolve(string id, CapabilityGateContext context);
}

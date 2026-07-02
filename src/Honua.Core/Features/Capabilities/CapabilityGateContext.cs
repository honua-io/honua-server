// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;

namespace Honua.Core.Features.Capabilities;

/// <summary>
/// The inputs a capability resolution is evaluated against — the seam that the
/// config-driven experimental gate resolver (#2339 / T2) and the per-environment
/// manifest resolver (#2335 / B3) fill in. In B1 the context is accepted by
/// <see cref="ICapabilityRegistry.Resolve"/> but not yet consulted (resolution is
/// a pass-through); it is defined now so its shape is frozen alongside
/// <see cref="CapabilityDescriptor"/>.
/// </summary>
/// <remarks>
/// New gate inputs (experimental flag overrides, tenant scope, config snapshot)
/// are added here as additive <c>init</c> properties by the follow-up tickets.
/// </remarks>
public sealed record CapabilityGateContext
{
    /// <summary>The active platform edition the capability is evaluated for.</summary>
    public HonuaEdition Edition { get; init; } = HonuaEdition.Community;

    /// <summary>
    /// The deployment environment name (for example <c>production</c>), or
    /// <c>null</c> when the evaluation is not environment-scoped.
    /// </summary>
    public string? DeploymentEnvironment { get; init; }

    /// <summary>
    /// A default context (Community edition, no environment scope) for callers
    /// that have no richer context — primarily the B1 pass-through and tests.
    /// </summary>
    public static CapabilityGateContext Default { get; } = new();
}

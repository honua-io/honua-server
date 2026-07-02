// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp;

/// <summary>
/// Options that bind the served <c>/mcp</c> catalog to the unified capability
/// registry (ADR-0058, Decision B / honua-server B2 #2334). Bound from the
/// <c>Capabilities</c> configuration section.
/// </summary>
public sealed class CapabilityRegistryBindingOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Capabilities";

    /// <summary>
    /// When <c>true</c> (the default, and on in CI), the host runs a startup
    /// composition conformance check that fails fast if the served <c>/mcp</c>
    /// tool/resource catalog contains an entry the
    /// <see cref="Honua.Core.Features.Capabilities.ICapabilityRegistry"/> does not
    /// describe. Set <c>Capabilities:RegistryBinding=false</c> to disable the gate,
    /// for example a bespoke host that intentionally serves a surface the shared
    /// registry does not yet mirror.
    /// </summary>
    public bool RegistryBinding { get; set; } = true;
}

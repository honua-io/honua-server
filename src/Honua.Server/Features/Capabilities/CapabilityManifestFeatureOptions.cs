// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Capabilities;

/// <summary>
/// Staged feature switches for how the <c>honua.capability_manifest.v1</c> surface
/// (#1186) is composed (#2335 / B3).
/// </summary>
internal sealed class CapabilityManifestFeatureOptions
{
    /// <summary>The configuration key of the registry-derivation switch.</summary>
    public const string FromRegistryConfigKey = "Capabilities:ManifestFromRegistry";

    /// <summary>
    /// When <c>true</c>, the manifest's <c>Capabilities[]</c> and
    /// <c>Packages.Families[]</c> are derived by iterating the unified
    /// <see cref="Core.Features.Capabilities.ICapabilityRegistry"/> through the
    /// <see cref="Core.Features.Capabilities.CapabilityGateResolver"/>
    /// (experimental-disabled descriptors are omitted, which is wire-compatible)
    /// rather than the hand-curated in-service lists (ADR-0058, Decision B / B3).
    /// </summary>
    /// <remarks>
    /// Defaults to <c>false</c> (staged): the served manifest keeps the legacy
    /// hand-curated composition until this flag is turned on. While every registry
    /// descriptor stays <see cref="Core.Features.Capabilities.CapabilityMaturity.Implemented"/>,
    /// the two paths produce a byte-identical wire document; the flag exists so the
    /// registry-derived path can be staged into production without a wire change, and
    /// so the experimental omission (T10 / #2346) has a mechanism to act on.
    /// </remarks>
    public bool FromRegistry { get; set; }
}

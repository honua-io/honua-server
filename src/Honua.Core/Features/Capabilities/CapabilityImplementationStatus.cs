// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Capabilities;

/// <summary>
/// Whether the live surface actually serves a capability, or declares it as an
/// honest gap. This mirrors the <c>known-gap</c> concept the
/// <c>CapabilityManifestEmitter</c> (#2338 / ADR-0058 "Derive, don't fork")
/// records in the vendored <c>geospatial-mcp</c> index rather than silently
/// advertising unserved sub-families.
/// </summary>
public enum CapabilityImplementationStatus
{
    /// <summary>The capability is served by the live surface today.</summary>
    Served = 0,

    /// <summary>
    /// A declared but not-served gap — advertised as <c>known-gap</c> in the
    /// conformance manifest so "what the platform claims" stays equal to "what it
    /// serves". No descriptor in B1 uses this value (B1 mirrors only what exists).
    /// </summary>
    KnownGap = 1,
}

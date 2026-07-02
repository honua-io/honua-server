// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Capabilities;

/// <summary>
/// The product-lifecycle maturity of a capability. This is the <b>shared</b>
/// maturity enum for the platform: the unified capability registry (ADR-0058)
/// and the evidence-based feature-catalog generator (ADR-0054, consumed in T9)
/// both project from it, so it lives in <c>Honua.Core</c> where both can
/// reference it. The ordering is deliberately least-to-most mature so callers
/// can compare with <c>&gt;=</c>.
/// </summary>
public enum CapabilityMaturity
{
    /// <summary>Planned but not yet built; declared for roadmap visibility only.</summary>
    Planned = 0,

    /// <summary>
    /// Deferred out of the current release scope; present on trunk but not
    /// advertised/served (ADR-0058 defers temporal, disconnected-sync, and others).
    /// </summary>
    Deferred = 1,

    /// <summary>
    /// Experimental — implemented but off by default and config-gated; the
    /// experimental flag precedence lands in #2339 (T2) and the flips in T10 (#2346).
    /// </summary>
    Experimental = 2,

    /// <summary>Partially implemented — a subset of the capability is served.</summary>
    Partial = 3,

    /// <summary>Fully implemented and shipped in the current live surface.</summary>
    Implemented = 4,
}

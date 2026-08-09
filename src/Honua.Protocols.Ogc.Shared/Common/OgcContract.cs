// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.Ogc.Common;

/// <summary>
/// The rolled-up wire-contract version for Honua's OGC API surface (OGC API - Common /
/// Features / Tiles / Maps / Processes, plus the OGC classic WMS/WMTS/WFS/WCS/WPS
/// facades). ADR-0058 makes this the single seam that names the surface's contract
/// version so the capability manifest can advertise a real value for the surface rather
/// than nothing (release gate honua-release#32).
/// </summary>
/// <remarks>
/// The individual OGC API building blocks Honua serves are each pinned at the OGC
/// <c>1.0</c> core (see <see cref="OgcConformanceUris"/> and the per-endpoint conformance
/// declarations); this constant is the single rolled-up contract version for the whole
/// OGC surface, bumped only on a breaking change to that rolled-up wire contract.
/// </remarks>
public static class OgcContract
{
    /// <summary>The rolled-up OGC API wire-contract version (semver).</summary>
    public const string Version = "1.0.0";
}

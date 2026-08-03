// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices;

/// <summary>
/// The rolled-up wire-contract version for Honua's GeoServices REST surface (the
/// Esri-compatible service directory, <c>/rest/info</c>, and the FeatureServer /
/// MapServer / ImageServer / GPServer / VectorTileServer / VersionManagementServer
/// / GeometryService families). ADR-0058 makes this the single seam that names the
/// surface's contract version so the capability manifest can advertise a real value
/// for the surface rather than nothing (release gate honua-release#32).
/// </summary>
/// <remarks>
/// This is Honua's own contract version for its independent Esri-compatible surface.
/// It is deliberately <b>not</b> an ArcGIS Server / Portal release number: Honua does
/// not impersonate an ArcGIS release, and none of the GeoServices wire models advertise
/// a <c>currentVersion</c>/<c>fullVersion</c> field (guarded by
/// <c>NoArcGisServerVersionTests</c>). The value is bumped only on a breaking change to
/// the rolled-up GeoServices wire contract.
/// </remarks>
public static class GeoServicesContract
{
    /// <summary>The rolled-up GeoServices REST wire-contract version (semver).</summary>
    public const string Version = "1.0.0";
}

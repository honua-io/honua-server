// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.Stac.Models;

/// <summary>
/// The rolled-up wire-contract version for Honua's STAC API surface (catalog,
/// collections, items, and item-search). ADR-0058 makes this the single seam that names
/// the surface's contract version so the capability manifest can advertise a real value
/// for the surface rather than nothing (release gate honua-release#32).
/// </summary>
/// <remarks>
/// The value is the STAC specification version Honua implements — the same
/// <see cref="StacConstants.StacVersion"/> already emitted as <c>stac_version</c> on every
/// STAC document — surfaced here as the public rolled-up contract version so downstream
/// registry/manifest consumers do not have to reach into the internal STAC constants.
/// </remarks>
public static class StacContract
{
    /// <summary>The rolled-up STAC API wire-contract version (the implemented STAC spec version).</summary>
    public const string Version = StacConstants.StacVersion;
}

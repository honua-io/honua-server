// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Portal.Domain;

namespace Honua.Protocols.GeoServices.Sharing;

/// <summary>
/// Response body for <c>/sharing/rest/info</c> (and the <c>/rest/info</c> alias).
/// Mirrors the ArcGIS Portal <c>info</c> document so packaged Esri clients can
/// discover the portal version and the supported authentication scheme.
/// </summary>
internal sealed record SharingInfoResponse
{
    // No ArcGIS Portal version (currentVersion/fullVersion) is advertised. Honua is an
    // independent, Esri-compatible server and must not impersonate a specific ArcGIS
    // Portal/Server release. Do NOT add a currentVersion/fullVersion field
    // (guarded by NoArcGisServerVersionTests).

    /// <summary>
    /// Authentication metadata advertising token-based security and the
    /// token-issuance endpoint.
    /// </summary>
    [JsonPropertyName("authInfo")]
    public required SharingAuthInfo AuthInfo { get; init; }
}

/// <summary>
/// Authentication metadata block embedded in <see cref="SharingInfoResponse"/>.
/// </summary>
internal sealed record SharingAuthInfo
{
    /// <summary>
    /// Whether the portal uses token-based security.
    /// </summary>
    [JsonPropertyName("isTokenBasedSecurity")]
    public bool IsTokenBasedSecurity { get; init; } = true;

    /// <summary>
    /// Absolute URL of the token-issuance endpoint (<c>/sharing/rest/generateToken</c>),
    /// honouring the forwarded scheme/host.
    /// </summary>
    [JsonPropertyName("tokenServicesUrl")]
    public string TokenServicesUrl { get; init; } = string.Empty;
}

/// <summary>
/// Response body for <c>/sharing/rest/portals/self</c>. A minimal, RBAC-scoped
/// projection of the portal the caller is connected to.
/// </summary>
internal sealed record PortalSelfResponse
{
    /// <summary>Stable portal identifier.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Whether the caller authenticated.</summary>
    [JsonPropertyName("isPortal")]
    public bool IsPortal { get; init; } = true;

    /// <summary>Portal display name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Portal short name / URL key.</summary>
    [JsonPropertyName("portalName")]
    public string PortalName { get; init; } = "Honua";

    /// <summary>
    /// Authenticated user document, or <see langword="null"/> for anonymous callers.
    /// </summary>
    [JsonPropertyName("user")]
    public PortalUser? User { get; init; }

    /// <summary>
    /// Utility services this portal offers, keyed the way Esri clients expect.
    /// <see langword="null"/> when none are available, so the property is omitted rather
    /// than advertised empty.
    ///
    /// Esri clients discover routing through this document: <c>arcpy.nax</c> and ArcGIS
    /// Pro's Network Analyst pane resolve a signed-in portal to a solver by reading
    /// <c>helperServices.route</c>. Without it they report "Cannot use &lt;portal&gt; for
    /// network analysis" even when the NAServer endpoints answer correctly, which is what
    /// the 2026-09-21 certification run measured.
    /// </summary>
    [JsonPropertyName("helperServices")]
    public PortalHelperServices? HelperServices { get; init; }

    // No ArcGIS Portal version (currentVersion) is advertised — Honua does not impersonate a
    // specific ArcGIS Portal release (guarded by NoArcGisServerVersionTests).
}

/// <summary>
/// Response body for <c>/sharing/rest/community/self</c>. The RBAC-scoped user
/// document for the calling principal.
/// </summary>
internal sealed record CommunitySelfResponse
{
    /// <summary>Username of the calling principal.</summary>
    [JsonPropertyName("username")]
    public required string Username { get; init; }

    /// <summary>Display name of the calling principal.</summary>
    [JsonPropertyName("fullName")]
    public string FullName { get; init; } = string.Empty;

    /// <summary>Roles granted to the calling principal.</summary>
    [JsonPropertyName("role")]
    public string Role { get; init; } = "org_user";
}

/// <summary>
/// The user sub-document embedded in <see cref="PortalSelfResponse"/>.
/// </summary>
internal sealed record PortalUser
{
    /// <summary>Username of the calling principal.</summary>
    [JsonPropertyName("username")]
    public required string Username { get; init; }

    /// <summary>Display name of the calling principal.</summary>
    [JsonPropertyName("fullName")]
    public string FullName { get; init; } = string.Empty;

    /// <summary>Coarse role string for the calling principal.</summary>
    [JsonPropertyName("role")]
    public string Role { get; init; } = "org_user";
}

/// <summary>
/// Response envelope for <c>/sharing/rest/search</c>. Matches the ArcGIS Portal
/// search result shape (<c>total</c>/<c>start</c>/<c>num</c>/<c>nextStart</c>/
/// <c>results</c>) so Esri clients can page through visible items.
/// </summary>
internal sealed record SearchResponse
{
    /// <summary>The search query echoed back to the caller.</summary>
    [JsonPropertyName("query")]
    public string Query { get; init; } = string.Empty;

    /// <summary>Total number of items matching the (access-filtered) query.</summary>
    [JsonPropertyName("total")]
    public int Total { get; init; }

    /// <summary>1-based index of the first returned result.</summary>
    [JsonPropertyName("start")]
    public int Start { get; init; }

    /// <summary>Number of results in this page.</summary>
    [JsonPropertyName("num")]
    public int Num { get; init; }

    /// <summary>
    /// 1-based index of the next page's first result, or <c>-1</c> when this is the
    /// last page (the Esri sentinel).
    /// </summary>
    [JsonPropertyName("nextStart")]
    public int NextStart { get; init; }

    /// <summary>The projected, access-filtered items in this page.</summary>
    [JsonPropertyName("results")]
    public IReadOnlyList<PortalItem> Results { get; init; } = Array.Empty<PortalItem>();
}

/// <summary>
/// The <c>helperServices</c> block of <c>/sharing/rest/portals/self</c>. Each entry is
/// advertised only when the backing capability is genuinely available, so a client is
/// never pointed at a utility this deployment cannot perform.
/// </summary>
internal sealed record PortalHelperServices
{
    /// <summary>Route solver, or <see langword="null"/> when routing is unavailable.</summary>
    [JsonPropertyName("route")]
    public PortalHelperService? Route { get; init; }

    /// <summary>Service-area solver, or <see langword="null"/> when unavailable.</summary>
    [JsonPropertyName("serviceArea")]
    public PortalHelperService? ServiceArea { get; init; }

    /// <summary>Closest-facility solver, or <see langword="null"/> when unavailable.</summary>
    [JsonPropertyName("closestFacility")]
    public PortalHelperService? ClosestFacility { get; init; }
}

/// <summary>
/// A single advertised utility service.
/// </summary>
internal sealed record PortalHelperService
{
    /// <summary>Absolute URL of the service.</summary>
    [JsonPropertyName("url")]
    public required string Url { get; init; }
}

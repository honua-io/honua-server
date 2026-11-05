// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Portal.Domain;

namespace Honua.Protocols.GeoServices.Sharing;

internal sealed record SharingInfoResponse
{
    // No ArcGIS Portal version (currentVersion/fullVersion) is advertised. Honua is an
    // independent GeoServices REST server and must not impersonate a specific ArcGIS
    // Portal/Server release. Do NOT add a currentVersion/fullVersion field
    // (guarded by NoGeoServicesServerVersionTests).

    [JsonPropertyName("owningSystemUrl")]
    public required string OwningSystemUrl { get; init; }

    [JsonPropertyName("authInfo")]
    public required SharingAuthInfo AuthInfo { get; init; }
}

internal sealed record SharingRootResponse
{
    [JsonPropertyName("isPortal")]
    public bool IsPortal { get; init; } = true;
}

internal sealed record SharingAuthInfo
{
    [JsonPropertyName("isTokenBasedSecurity")]
    public bool IsTokenBasedSecurity { get; init; } = true;

    [JsonPropertyName("tokenServicesUrl")]
    public string TokenServicesUrl { get; init; } = string.Empty;
}

internal sealed record PortalSelfResponse
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("isPortal")]
    public bool IsPortal { get; init; } = true;

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("portalName")]
    public string PortalName { get; init; } = "Honua";

    [JsonPropertyName("user")]
    public PortalUser? User { get; init; }

    [JsonPropertyName("helperServices")]
    public PortalHelperServices HelperServices { get; init; } = new();

    // No ArcGIS Portal version (currentVersion) is advertised — Honua does not impersonate a
    // specific ArcGIS Portal release (guarded by NoGeoServicesServerVersionTests).
}

internal sealed record PortalHelperServices
{
    [JsonPropertyName("route")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PortalHelperService? Route { get; init; }

    [JsonPropertyName("serviceArea")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PortalHelperService? ServiceArea { get; init; }

    [JsonPropertyName("closestFacility")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PortalHelperService? ClosestFacility { get; init; }

    [JsonPropertyName("odCostMatrix")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PortalHelperService? OdCostMatrix { get; init; }
}

internal sealed record PortalHelperService
{
    [JsonPropertyName("url")]
    public required string Url { get; init; }
}

internal sealed record CommunitySelfResponse
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("username")]
    public required string Username { get; init; }

    [JsonPropertyName("fullName")]
    public string FullName { get; init; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; init; } = "org_user";

    [JsonPropertyName("groups")]
    public IReadOnlyList<GroupResponse> Groups { get; init; } = Array.Empty<GroupResponse>();
}

internal sealed record ContentUserResponse
{
    [JsonPropertyName("username")]
    public required string Username { get; init; }

    [JsonPropertyName("currentFolder")]
    public string? CurrentFolder { get; init; }

    [JsonPropertyName("folders")]
    public IReadOnlyList<object> Folders { get; init; } = Array.Empty<object>();

    [JsonPropertyName("items")]
    public IReadOnlyList<PortalItem> Items { get; init; } = Array.Empty<PortalItem>();
}

internal sealed record PortalUser
{
    [JsonPropertyName("username")]
    public required string Username { get; init; }

    [JsonPropertyName("fullName")]
    public string FullName { get; init; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; init; } = "org_user";
}

internal sealed record SearchResponse
{
    [JsonPropertyName("query")]
    public string Query { get; init; } = string.Empty;

    [JsonPropertyName("total")]
    public int Total { get; init; }

    [JsonPropertyName("start")]
    public int Start { get; init; }

    [JsonPropertyName("num")]
    public int Num { get; init; }

    [JsonPropertyName("nextStart")]
    public int NextStart { get; init; }

    [JsonPropertyName("results")]
    public IReadOnlyList<PortalItem> Results { get; init; } = Array.Empty<PortalItem>();
}

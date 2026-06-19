// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.Catalog;

internal sealed record ServicesDirectoryResponse
{
    // No ArcGIS Server version is advertised. Honua is an independent, Esri-compatible
    // server and must not impersonate a specific ArcGIS Server release. Do NOT add a
    // currentVersion/fullVersion field (guarded by NoArcGisServerVersionTests).

    [JsonPropertyName("folders")]
    public string[] Folders { get; init; } = [];

    [JsonPropertyName("services")]
    public ServiceDirectoryEntry[] Services { get; init; } = [];
}

internal sealed record ServiceDirectoryEntry
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("url")]
    public required string Url { get; init; }
}

internal sealed record RestInfoResponse
{
    // No currentVersion/fullVersion — Honua does not advertise an ArcGIS Server version.

    [JsonPropertyName("authInfo")]
    public RestAuthInfo AuthInfo { get; init; } = new();
}

internal sealed record RestAuthInfo
{
    [JsonPropertyName("isTokenBasedSecurity")]
    public bool IsTokenBasedSecurity { get; init; }
}

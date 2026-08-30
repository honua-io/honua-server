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

    [JsonIgnore]
    public string? SoapCapabilities { get; init; }
}

internal sealed record RestInfoResponse
{
    /// <summary>
    /// ArcGIS REST compatibility level selected for native Esri clients. ArcGIS Pro 3.7's
    /// ImageServer reader rejects the service before issuing an image operation when this
    /// field is absent. This is not Honua's product version; keep the exception confined to
    /// <c>/rest/info</c>, and do not add <c>fullVersion</c> or version fields to service models.
    /// See honua-server#3375 for the licensed-client A/B evidence.
    /// </summary>
    [JsonPropertyName("currentVersion")]
    public double CurrentVersion { get; init; } = 10.8;

    [JsonPropertyName("soapUrl")]
    public required string SoapUrl { get; init; }

    [JsonPropertyName("secureSoapUrl")]
    public string? SecureSoapUrl { get; init; }

    [JsonPropertyName("authInfo")]
    public RestAuthInfo AuthInfo { get; init; } = new();
}

internal sealed record RestAuthInfo
{
    [JsonPropertyName("isTokenBasedSecurity")]
    public bool IsTokenBasedSecurity { get; init; }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.Catalog;

internal sealed record ServicesDirectoryResponse
{
    // No ArcGIS Server version is advertised. Honua is an independent GeoServices REST
    // server and must not impersonate a specific ArcGIS Server release. Do NOT add a
    // currentVersion/fullVersion field (guarded by NoGeoServicesServerVersionTests).

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
    public string? LogicalName { get; init; }

    [JsonIgnore]
    public string? FolderName { get; init; }

    [JsonIgnore]
    public string? DisplayName { get; init; }

    [JsonIgnore]
    public string? SoapCapabilities { get; init; }
}

internal sealed record RestInfoResponse
{
    // ArcGIS Pro 3.7's ImageServer reader rejects the service when this compatibility field is absent; it is not Honua's product version, so fullVersion and version stay off the service models and this field stays on /rest/info.
    [JsonPropertyName("currentVersion")]
    public double CurrentVersion { get; init; } = 10.8;

    [JsonPropertyName("owningSystemUrl")]
    public required string OwningSystemUrl { get; init; }

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
    public bool IsTokenBasedSecurity { get; init; } = true;

    [JsonPropertyName("tokenServicesUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TokenServicesUrl { get; init; }
}

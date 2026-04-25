// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Protocols.GeoServices.Catalog;

internal sealed record ServicesDirectoryResponse
{
    [JsonPropertyName("currentVersion")]
    public double CurrentVersion { get; init; } = 10.81;

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
    [JsonPropertyName("currentVersion")]
    public double CurrentVersion { get; init; } = 10.81;

    [JsonPropertyName("fullVersion")]
    public string FullVersion { get; init; } = "10.81";

    [JsonPropertyName("authInfo")]
    public RestAuthInfo AuthInfo { get; init; } = new();
}

internal sealed record RestAuthInfo
{
    [JsonPropertyName("isTokenBasedSecurity")]
    public bool IsTokenBasedSecurity { get; init; }
}

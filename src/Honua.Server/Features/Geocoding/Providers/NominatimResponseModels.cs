// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Geocoding.Providers;

internal sealed class NominatimSearchResult
{
    [JsonPropertyName("place_id")]
    public long PlaceId { get; init; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("lat")]
    public string? Latitude { get; init; }

    [JsonPropertyName("lon")]
    public string? Longitude { get; init; }

    [JsonPropertyName("importance")]
    public double? Importance { get; init; }

    [JsonPropertyName("address")]
    public Dictionary<string, string?>? Address { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(NominatimSearchResult[]))]
[JsonSerializable(typeof(NominatimSearchResult))]
internal sealed partial class NominatimJsonContext : JsonSerializerContext
{
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Geocoding;

internal sealed record GeocodeProviderCapabilities(
    bool SupportsSuggest,
    bool SupportsBatch,
    bool SupportsStructuredInput,
    bool SupportsBiasing);

internal sealed record ForwardGeocodeRequest(
    string Query,
    int MaxResults,
    int SpatialReferenceWkid,
    string? CountryCodes);

internal sealed record ReverseGeocodeRequest(
    double X,
    double Y,
    int SpatialReferenceWkid);

internal sealed record SuggestGeocodeRequest(
    string Text,
    int MaxResults,
    string? CountryCodes);

internal sealed record BatchGeocodeRequest(
    IReadOnlyList<string> Queries,
    int SpatialReferenceWkid);

internal sealed record GeocodeCandidate(
    string Address,
    double X,
    double Y,
    double Score,
    IReadOnlyDictionary<string, string?> Attributes,
    string? ProviderId = null,
    double? DistanceMeters = null);

internal sealed record ReverseGeocodeMatch(
    string Address,
    double X,
    double Y,
    IReadOnlyDictionary<string, string?> Attributes,
    string? ProviderId = null,
    double? DistanceMeters = null);

internal sealed record GeocodeSuggestion(
    string Text,
    string MagicKey,
    bool IsCollection = false);

internal sealed record GeocodeServerInfoResponse
{
    [JsonPropertyName("currentVersion")]
    public double CurrentVersion { get; init; } = 10.81;

    [JsonPropertyName("serviceDescription")]
    public string ServiceDescription { get; init; } = "Honua GeocodeServer";

    [JsonPropertyName("singleLineAddressField")]
    public GeocodeAddressField SingleLineAddressField { get; init; } = new()
    {
        Name = "SingleLine",
        Alias = "Single Line Input"
    };

    [JsonPropertyName("addressFields")]
    public GeocodeAddressField[] AddressFields { get; init; } =
    [
        new GeocodeAddressField { Name = "Address", Alias = "Address" },
        new GeocodeAddressField { Name = "City", Alias = "City" },
        new GeocodeAddressField { Name = "Region", Alias = "Region" },
        new GeocodeAddressField { Name = "Postal", Alias = "Postal" },
        new GeocodeAddressField { Name = "CountryCode", Alias = "Country Code" }
    ];

    [JsonPropertyName("capabilities")]
    public required string Capabilities { get; init; }

    [JsonPropertyName("locatorProperties")]
    public Dictionary<string, string> LocatorProperties { get; init; } = new(StringComparer.Ordinal)
    {
        ["LocatorName"] = "World",
        ["SuggestedBatchSize"] = "100"
    };

    [JsonPropertyName("spatialReference")]
    public required GeocodeSpatialReference SpatialReference { get; init; }
}

internal sealed record GeocodeAddressField
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("alias")]
    public required string Alias { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = "esriFieldTypeString";

    [JsonPropertyName("length")]
    public int Length { get; init; } = 255;
}

internal sealed record GeocodeSpatialReference
{
    [JsonPropertyName("wkid")]
    public required int Wkid { get; init; }

    [JsonPropertyName("latestWkid")]
    public required int LatestWkid { get; init; }
}

internal sealed record GeocodePoint
{
    [JsonPropertyName("x")]
    public required double X { get; init; }

    [JsonPropertyName("y")]
    public required double Y { get; init; }

    [JsonPropertyName("spatialReference")]
    public required GeocodeSpatialReference SpatialReference { get; init; }
}

internal sealed record FindAddressCandidatesResponse
{
    [JsonPropertyName("spatialReference")]
    public required GeocodeSpatialReference SpatialReference { get; init; }

    [JsonPropertyName("candidates")]
    public required GeocodeCandidateResponse[] Candidates { get; init; }
}

internal sealed record GeocodeCandidateResponse
{
    [JsonPropertyName("address")]
    public required string Address { get; init; }

    [JsonPropertyName("location")]
    public required GeocodePoint Location { get; init; }

    [JsonPropertyName("score")]
    public required double Score { get; init; }

    [JsonPropertyName("attributes")]
    public required IReadOnlyDictionary<string, string?> Attributes { get; init; }
}

internal sealed record ReverseGeocodeResponse
{
    [JsonPropertyName("address")]
    public required IReadOnlyDictionary<string, string?> Address { get; init; }

    [JsonPropertyName("location")]
    public required GeocodePoint Location { get; init; }
}

internal sealed record SuggestResponse
{
    [JsonPropertyName("suggestions")]
    public required GeocodeSuggestionResponse[] Suggestions { get; init; }
}

internal sealed record GeocodeSuggestionResponse
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("magicKey")]
    public required string MagicKey { get; init; }

    [JsonPropertyName("isCollection")]
    public bool IsCollection { get; init; }
}

internal sealed record GeocodeAddressesResponse
{
    [JsonPropertyName("spatialReference")]
    public required GeocodeSpatialReference SpatialReference { get; init; }

    [JsonPropertyName("locations")]
    public required GeocodeAddressLocation[] Locations { get; init; }
}

internal sealed record GeocodeAddressLocation
{
    [JsonPropertyName("address")]
    public required string Address { get; init; }

    [JsonPropertyName("location")]
    public required GeocodePoint Location { get; init; }

    [JsonPropertyName("score")]
    public required double Score { get; init; }

    [JsonPropertyName("attributes")]
    public required IReadOnlyDictionary<string, string?> Attributes { get; init; }
}

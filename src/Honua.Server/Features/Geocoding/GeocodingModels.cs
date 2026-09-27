// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Geocoding;

internal sealed record GeocodeProviderCapabilities(
    bool SupportsSuggest,
    bool SupportsBatch,
    bool SupportsStructuredInput,
    bool SupportsBiasing);

/// <summary>
/// The category tokens GeocodeServer advertises for <c>category</c> filtering. They mirror the
/// address-type families Honua's backing providers classify candidates and suggestions into
/// (see each provider's <c>GetAddressType</c> mapping), so filtering only ever matches data the
/// provider already returns.
/// </summary>
internal static class GeocodeSupportedCategories
{
    public static readonly string[] All =
    [
        "Address",
        "PointAddress",
        "StreetAddress",
        "POI",
        "Neighborhood",
        "Locality",
        "City",
        "Subregion",
        "County",
        "State",
        "Country",
        "PostalCode"
    ];
}

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
    // No ArcGIS Server version (currentVersion/fullVersion) is advertised. Honua is an
    // independent, Esri-compatible server and must not impersonate a specific ArcGIS Server
    // release. Do NOT add a currentVersion/fullVersion field (guarded by
    // NoHonuaServerArcGisVersionTests / NoArcGisServerVersionTests).

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

    // Output fields present on every candidate's attribute bag. ArcGIS clients
    // introspect candidateFields to discover the result schema; Honua advertises
    // only the fields it consistently emits rather than a full Esri locator schema.
    [JsonPropertyName("candidateFields")]
    public GeocodeAddressField[] CandidateFields { get; init; } =
    [
        new GeocodeAddressField { Name = "Match_addr", Alias = "Match Address" },
        new GeocodeAddressField { Name = "Provider", Alias = "Provider" }
    ];

    // The category tokens findAddressCandidates/suggest accept to narrow results by the
    // provider-supplied address type. Filtering runs on the shared geocode interface against the
    // category data providers return, so the advertised set is the canonical address/place
    // families Honua's providers classify candidates into.
    [JsonPropertyName("categories")]
    public string[] Categories { get; init; } = GeocodeSupportedCategories.All;

    // Populated per active provider by the handler (#2147): SuggestedBatchSize is derived from the
    // provider's MaxBatchSize and is present only when batch is supported. The default carries just
    // the locator name so an unconfigured response never falsely advertises a batch capability.
    [JsonPropertyName("locatorProperties")]
    public Dictionary<string, string> LocatorProperties { get; init; } = new(StringComparer.Ordinal)
    {
        ["LocatorName"] = "World"
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
    // Esri geocodeAddresses reports a ResultID on every location that correlates it back to
    // the submitted record (the OBJECTID/ResultID supplied in the request). Honua assigns the
    // zero-based input index so clients can map each location to its input even when an input
    // produced no match. Emitted first to mirror the Esri response field ordering.
    [JsonPropertyName("resultId")]
    public required int ResultId { get; init; }

    [JsonPropertyName("address")]
    public required string Address { get; init; }

    // Null for unmatched records (blank or zero-candidate inputs). The slot is still emitted so
    // the locations array stays 1:1 and in order with the submitted records.
    [JsonPropertyName("location")]
    public required GeocodePoint? Location { get; init; }

    [JsonPropertyName("score")]
    public required double Score { get; init; }

    [JsonPropertyName("attributes")]
    public required IReadOnlyDictionary<string, string?> Attributes { get; init; }
}

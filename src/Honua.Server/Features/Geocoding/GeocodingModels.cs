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
        Alias = "Single Line Input",

        // 300, as the live World locator declares. The tools size the input column they
        // map "SingleLine SingleLine VISIBLE NONE" onto from this.
        Length = 300
    };

    [JsonPropertyName("addressFields")]
    public GeocodeAddressField[] AddressFields { get; init; } =
    [
        // Widths match the live World locator's addressFields.
        new GeocodeAddressField { Name = "Address", Alias = "Address", Length = 150 },
        new GeocodeAddressField { Name = "City", Alias = "City", Length = 100 },
        new GeocodeAddressField { Name = "Region", Alias = "Region", Length = 100 },
        new GeocodeAddressField { Name = "Postal", Alias = "Postal", Length = 20 },
        new GeocodeAddressField { Name = "CountryCode", Alias = "Country Code", Length = 100 }
    ];

    [JsonPropertyName("capabilities")]
    public required string Capabilities { get; init; }

    // Output fields present on every candidate's attribute bag. ArcGIS clients
    // introspect candidateFields to discover the result schema, and
    // arcpy.geocoding.GeocodeAddresses builds its output table from it - so declaring a
    // subset is not a conservative choice, it is a wrong one. With only Match_addr and
    // Provider declared while findAddressCandidates returned twenty-nine attributes, the
    // tool failed "ERROR 000010: Geocode addresses failed" against a locator it had
    // already bound and described as an AddressLocator, because it could not map a
    // result schema it had been told nothing about - Addr_type in particular, which the
    // geocoding tools always map. Refs honua-server#5145.
    //
    // Every documented field is declared, matching what EsriGeocodeAddressFields emits
    // on every candidate. The two must agree: this is the same defect as the query
    // response and the layer resource disagreeing about a field (#5197).
    [JsonPropertyName("candidateFields")]
    public GeocodeAddressField[] CandidateFields { get; init; } =
    [
        // The first four are the OUTPUT schema, not address components, and their absence
        // is why arcpy.geocoding.GeocodeAddresses failed: the tool builds its result
        // feature class from candidateFields, and without a geometry, a Status and a
        // Score marked required it has no output to construct. Verified against the live
        // ArcGIS World Geocoding Service document, which declares all four this way and
        // carries `required` on every field. Refs honua-server#5145.
        new GeocodeAddressField { Name = "Loc_name", Alias = "Loc_name", Length = 20 },
        new GeocodeAddressField { Name = "Shape", Alias = "Shape", Type = "esriFieldTypeGeometry", Length = null, Required = true },
        new GeocodeAddressField { Name = "Status", Alias = "Status", Length = 1, Required = true },
        new GeocodeAddressField { Name = "Score", Alias = "Score", Type = "esriFieldTypeDouble", Length = null, Required = true },
        new GeocodeAddressField { Name = "Match_addr", Alias = "Match Address", Length = 500, Required = true },
        new GeocodeAddressField { Name = "LongLabel", Alias = "Long Label", Length = 500 },
        new GeocodeAddressField { Name = "ShortLabel", Alias = "Short Label", Length = 500 },
        new GeocodeAddressField { Name = "Addr_type", Alias = "Address Type", Length = 20 },
        new GeocodeAddressField { Name = "Type", Alias = "Type", Length = 50 },
        new GeocodeAddressField { Name = "PlaceName", Alias = "Place Name", Length = 200 },
        new GeocodeAddressField { Name = "AddNum", Alias = "Address Number", Length = 50 },
        new GeocodeAddressField { Name = "Address", Alias = "Address", Length = 150 },
        new GeocodeAddressField { Name = "Block", Alias = "Block", Length = 120 },
        new GeocodeAddressField { Name = "Sector", Alias = "Sector", Length = 120 },
        new GeocodeAddressField { Name = "Neighborhood", Alias = "Neighborhood", Length = 120 },
        new GeocodeAddressField { Name = "District", Alias = "District", Length = 120 },
        new GeocodeAddressField { Name = "City", Alias = "City", Length = 120 },
        new GeocodeAddressField { Name = "MetroArea", Alias = "Metro Area", Length = 120 },
        new GeocodeAddressField { Name = "Subregion", Alias = "Subregion", Length = 120 },
        new GeocodeAddressField { Name = "Region", Alias = "Region", Length = 120 },
        new GeocodeAddressField { Name = "Territory", Alias = "Territory", Length = 120 },
        new GeocodeAddressField { Name = "Postal", Alias = "Postal", Length = 20 },
        new GeocodeAddressField { Name = "PostalExt", Alias = "Postal Extension", Length = 10 },
        new GeocodeAddressField { Name = "CountryCode", Alias = "Country Code", Length = 100 },
        new GeocodeAddressField { Name = "Provider", Alias = "Provider", Length = 120 }
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

    /// <summary>
    /// Maximum width of the column the geocoding tools create for this field.
    /// </summary>
    /// <remarks>
    /// Omitted entirely for the non-string types, which is what a real locator document
    /// does: <c>Shape</c> and <c>Score</c> carry no length there.
    /// </remarks>
    [JsonPropertyName("length")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Length { get; init; } = 255;

    /// <summary>
    /// Whether the geocoding tools must create this column on their output.
    /// </summary>
    /// <remarks>
    /// Carried by every field of a real locator document. `Shape`, `Status` and `Score`
    /// are required there; everything else is optional.
    /// </remarks>
    [JsonPropertyName("required")]
    public bool Required { get; init; }
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
    public required IReadOnlyDictionary<string, GeocodeAttributeValue> Attributes { get; init; }
}

internal sealed record ReverseGeocodeResponse
{
    [JsonPropertyName("address")]
    public required IReadOnlyDictionary<string, GeocodeAttributeValue> Address { get; init; }

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
    public required IReadOnlyDictionary<string, GeocodeAttributeValue> Attributes { get; init; }
}

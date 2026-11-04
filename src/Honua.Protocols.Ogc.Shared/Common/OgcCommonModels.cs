// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Shared.Models;

namespace Honua.Protocols.Ogc.Common;

public sealed record LandingPage
{
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("x-honua-3d-supported")]
    public bool? Supports3d { get; init; }

    [JsonPropertyName("extent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Extent? Extent { get; init; }

    [JsonPropertyName("crs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<string>? Crs { get; init; }

    [JsonPropertyName("storageCrs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StorageCrs { get; init; }

    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }
}

public sealed record ConformanceDeclaration
{
    [JsonPropertyName("conformsTo")]
    public required ImmutableArray<string> ConformsTo { get; init; }

    [JsonPropertyName("links")]
    public ImmutableArray<Link>? Links { get; init; }
}

public sealed record Link : ILink
{
    [JsonPropertyName("href")]
    public required string Href { get; init; }

    [JsonPropertyName("rel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Rel { get; init; }

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    // The templated flag is omitted when unset, because the OGC link schema types it as boolean and a JSON null is non-conformant.
    [JsonPropertyName("templated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Templated { get; init; }

    [JsonPropertyName("hreflang")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HrefLang { get; init; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    [JsonPropertyName("method")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Method { get; init; }

    [JsonPropertyName("merge")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Merge { get; init; }

    [JsonPropertyName("body")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Body { get; init; }

    public static Link Create(string href, string? rel = null, string? type = null, string? title = null, string? method = null)
        => new() { Href = href, Rel = rel, Type = type, Title = title, Method = method };
}

public static class RelationTypes
{
    public const string Self = "self";

    public const string Alternate = "alternate";

    public const string DescribedBy = "describedby";

    public const string License = "license";

    public const string Data = "data";

    public const string Items = "items";

    public const string Style = "style";

    public const string Stylesheet = "stylesheet";

    public const string Preview = "preview";

    public const string Styles = "http://www.opengis.net/def/rel/ogc/1.0/styles";

    public const string ServiceDoc = "service-doc";

    public const string ServiceDesc = "service-desc";

    public const string Conformance = "conformance";

    public const string Collection = "collection";

    public const string Next = "next";

    public const string Prev = "prev";

    public const string Monitor = "monitor";

    public const string Queryables = "http://www.opengis.net/def/rel/ogc/1.0/queryables";

    public const string Schema = "http://www.opengis.net/def/rel/ogc/1.0/schema";

    public const string Coverage = "http://www.opengis.net/def/rel/ogc/1.0/coverage";

    public const string TilesetsVector = "http://www.opengis.net/def/rel/ogc/1.0/tilesets-vector";

    public const string TilesetsMap = "http://www.opengis.net/def/rel/ogc/1.0/tilesets-map";

    public const string Map = "https://www.opengis.net/def/rel/ogc/1.0/map";

    public const string TilesetsCoverage = "http://www.opengis.net/def/rel/ogc/1.0/tilesets-coverage";

    public const string TilingScheme = "http://www.opengis.net/def/rel/ogc/1.0/tiling-scheme";

    public const string Geodata = "http://www.opengis.net/def/rel/ogc/1.0/geodata";

    public const string OgcResults = "http://www.opengis.net/def/rel/ogc/1.0/results";
}

public sealed record Extent
{
    [JsonPropertyName("spatial")]
    public SpatialExtent? Spatial { get; init; }

    [JsonPropertyName("temporal")]
    public TemporalExtent? Temporal { get; init; }
}

public sealed record SpatialExtent
{
    [JsonPropertyName("bbox")]
    public required ImmutableArray<ImmutableArray<double>> BoundingBox { get; init; }

    [JsonPropertyName("storageCrsBbox")]
    public ImmutableArray<ImmutableArray<double>>? StorageCrsBoundingBox { get; init; }

    [JsonPropertyName("crs")]
    public string Crs { get; init; } = "http://www.opengis.net/def/crs/OGC/1.3/CRS84";
}

public sealed record TemporalExtent
{
    [JsonPropertyName("interval")]
    public required ImmutableArray<ImmutableArray<string?>> Interval { get; init; }

    [JsonPropertyName("trs")]
    public string Trs { get; init; } = "http://www.opengis.net/def/uom/ISO-8601/0/Gregorian";
}

public static class MediaTypes
{
    public const string Json = "application/json";

    public const string GeoJson = "application/geo+json";

    public const string Html = "text/html";

    public const string Csv = "text/csv";

    public const string OpenApi = "application/vnd.oai.openapi+json;version=3.0";

    public const string SchemaJson = "application/schema+json";

    // The GML media type keeps a space after the semicolon, because RFC 2045 and RFC 9110 require it in parameter lists.
    public const string Gml = "application/gml+xml; version=3.2";

    public const string Mvt = "application/vnd.mapbox-vector-tile";

    public const string Png = "image/png";

    public const string MapboxStyle = "application/vnd.mapbox.style+json";

    public const string Sld10 = "application/vnd.ogc.sld+xml;version=1.0";

    public const string Sld11 = "application/vnd.ogc.sld+xml;version=1.1";

    public const string GeoServicesDrawingInfo = "application/vnd.esri.drawinginfo+json";
}

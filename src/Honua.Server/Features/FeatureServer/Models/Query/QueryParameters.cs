// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.FeatureServer.Models;

/// <summary>
/// Query parameters for feature queries
/// </summary>
public sealed class QueryParameters
{
    /// <summary>
    /// WHERE clause for attribute queries
    /// </summary>
    public string? Where { get; init; }

    /// <summary>
    /// Fields to return (comma-separated list)
    /// </summary>
    public string? OutFields { get; init; }

    /// <summary>
    /// Fields to order results by (comma-separated list with optional ASC/DESC)
    /// </summary>
    public string? OrderByFields { get; init; }

    /// <summary>
    /// Whether to return geometry
    /// </summary>
    public bool ReturnGeometry { get; init; } = true;

    /// <summary>
    /// Whether to return only object IDs
    /// </summary>
    public bool ReturnIdsOnly { get; init; }

    /// <summary>
    /// Whether to return only the total count
    /// </summary>
    public bool ReturnCountOnly { get; init; }

    /// <summary>
    /// Whether to return only the extent of matching features
    /// </summary>
    public bool ReturnExtentOnly { get; init; }

    /// <summary>
    /// Output format (json, pjson, geojson, pbf, fgb, parquet, arrow)
    /// </summary>
    public string F { get; init; } = "json";

    /// <summary>
    /// Indicates whether the request explicitly provided the <c>f</c> query parameter.
    /// </summary>
    public bool FormatSpecified { get; init; }

    /// <summary>
    /// Number of records to offset for pagination
    /// </summary>
    public int? ResultOffset { get; init; }

    /// <summary>
    /// Maximum number of records to return
    /// </summary>
    public int? ResultRecordCount { get; init; }

    /// <summary>
    /// Filter geometry in GeoServices JSON format for spatial queries
    /// </summary>
    [JsonConverter(typeof(RawJsonStringConverter))]
    public string? Geometry { get; init; }

    /// <summary>
    /// Input spatial reference for geometry (WKID or WKT)
    /// </summary>
    [JsonPropertyName("inSR")]
    [JsonConverter(typeof(RawJsonStringConverter))]
    public string? InSr { get; init; }

    /// <summary>
    /// Output spatial reference for response geometry (WKID or WKT)
    /// </summary>
    [JsonPropertyName("outSR")]
    [JsonConverter(typeof(RawJsonStringConverter))]
    public string? OutSr { get; init; }

    /// <summary>
    /// Type of filter geometry (esriGeometryPoint, esriGeometryPolygon, esriGeometryEnvelope)
    /// </summary>
    public string? GeometryType { get; init; }

    /// <summary>
    /// Spatial relationship for filter (esriSpatialRelIntersects, esriSpatialRelContains, esriSpatialRelWithin,
    /// esriSpatialRelCrosses, esriSpatialRelTouches, esriSpatialRelOverlaps, esriSpatialRelDisjoint,
    /// esriSpatialRelEquals, esriSpatialRelWithinDistance, esriSpatialRelBeyondDistance)
    /// </summary>
    public string? SpatialRel { get; init; }

    /// <summary>
    /// Distance value for distance-based spatial queries (esriSpatialRelWithinDistance, esriSpatialRelBeyondDistance).
    /// Required when using distance-based spatial relationships.
    /// </summary>
    public double? Distance { get; init; }

    /// <summary>
    /// Unit of measure for distance queries. Supported values: esriSRUnit_Meter (default),
    /// esriSRUnit_Foot, esriSRUnit_Kilometer, esriSRUnit_StatuteMile.
    /// </summary>
    public string? Units { get; init; }

    /// <summary>
    /// Number of nearest neighbors to return for KNN queries.
    /// When specified with a geometry, returns the K closest features to that geometry.
    /// </summary>
    public int? NearestCount { get; init; }

    /// <summary>
    /// Whether to include the computed distance value in results for nearest neighbor queries.
    /// When true, a "distance" field will be added to each feature's attributes.
    /// </summary>
    public bool ReturnDistance { get; init; }

    /// <summary>
    /// Whether to include centroid geometry for returned features.
    /// </summary>
    public bool ReturnCentroid { get; init; }

    /// <summary>
    /// Whether to return distinct values for the requested fields.
    /// </summary>
    public bool ReturnDistinctValues { get; init; }

    /// <summary>
    /// Whether to include Z values in the output geometry.
    /// </summary>
    public bool ReturnZ { get; init; }

    /// <summary>
    /// Whether to include M values in the output geometry.
    /// </summary>
    public bool ReturnM { get; init; }

    /// <summary>
    /// Whether to return true curve geometries.
    /// </summary>
    public bool ReturnTrueCurves { get; init; }

    /// <summary>
    /// Whether to return exceeded limit features in the response.
    /// </summary>
    public bool ReturnExceededLimitFeatures { get; init; }

    /// <summary>
    /// Array of object IDs to retrieve. When specified, only features with these IDs will be returned.
    /// This parameter provides an alternative to using a WHERE clause for object ID filtering.
    /// </summary>
    public long[]? ObjectIds { get; init; }

    /// <summary>
    /// Precision for geometry output coordinates.
    /// </summary>
    public int? GeometryPrecision { get; init; }

    /// <summary>
    /// Maximum allowable offset for geometry generalization.
    /// </summary>
    public double? MaxAllowableOffset { get; init; }

    /// <summary>
    /// Query result type (e.g., standard, tile).
    /// </summary>
    public string? ResultType { get; init; }

    /// <summary>
    /// Statistics definitions for outStatistics queries.
    /// </summary>
    public string? OutStatistics { get; init; }

    /// <summary>
    /// Fields used for grouping statistics.
    /// </summary>
    public string? GroupByFieldsForStatistics { get; init; }

    /// <summary>
    /// Having clause for statistics queries.
    /// </summary>
    public string? Having { get; init; }

    /// <summary>
    /// SQL format for the response.
    /// </summary>
    public string? SqlFormat { get; init; }

    /// <summary>
    /// Geodatabase version name.
    /// </summary>
    public string? GdbVersion { get; init; }

    /// <summary>
    /// Quantization parameters for geometry output.
    /// </summary>
    public string? QuantizationParameters { get; init; }

    /// <summary>
    /// Datum transformation parameter for output SR.
    /// </summary>
    public string? DatumTransformation { get; init; }

    /// <summary>
    /// Time instant or time extent for temporal queries.
    /// Supported formats:
    /// - Single time instant: "1199145600000" (Unix timestamp in milliseconds)
    /// - Time extent: "1199145600000,1230768000000" (start,end)
    /// - ISO 8601: "2008-01-01T00:00:00Z" or "2008-01-01T00:00:00Z,2009-01-01T00:00:00Z"
    /// </summary>
    public string? Time { get; init; }

    /// <summary>
    /// Temporal relationship for time-based queries.
    /// Supported values include: esriTimeRelationIntersects (default), esriTimeRelationOverlaps,
    /// esriTimeRelationWithin, esriTimeRelationContains, esriTimeRelationDisjoint,
    /// esriTimeRelationBefore, esriTimeRelationAfter, esriTimeRelationEquals,
    /// esriTimeRelationStarts, esriTimeRelationStartedBy, esriTimeRelationFinishes,
    /// esriTimeRelationFinishedBy, esriTimeRelationMeets, esriTimeRelationMetBy,
    /// esriTimeRelationOverlapsStartWithinEnd, esriTimeRelationOverlapsEndWithinStart.
    /// </summary>
    public string? TimeRelation { get; init; }
}

internal sealed class RawJsonStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Number:
                return reader.TryGetInt64(out var longValue)
                    ? longValue.ToString(CultureInfo.InvariantCulture)
                    : reader.GetDouble().ToString(CultureInfo.InvariantCulture);
            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
            {
                using var document = JsonDocument.ParseValue(ref reader);
                return document.RootElement.GetRawText();
            }
            case JsonTokenType.Null:
                return null;
            default:
                throw new JsonException("Unsupported JSON token for string conversion.");
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (value.StartsWith('{') || value.StartsWith('['))
        {
            using var document = JsonDocument.Parse(value);
            document.RootElement.WriteTo(writer);
            return;
        }

        writer.WriteStringValue(value);
    }
}

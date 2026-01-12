// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Server.Features.Ogc.Common;
using Honua.Server.Features.OgcFeatures.Models;

namespace Honua.Server.Features.OgcFeatures.Services;

/// <summary>
/// Provides response formatting and content negotiation services for OGC Features.
/// </summary>
internal static class OgcResponseFormatter
{
    /// <summary>
    /// Formats feature responses based on requested output format.
    /// </summary>
    public static IResult FormatFeatureResponse<T>(
        T payload,
        JsonTypeInfo<T> typeInfo,
        string outputFormat,
        string title)
    {
        if (string.Equals(outputFormat, MediaTypes.Html, StringComparison.OrdinalIgnoreCase))
        {
            var json = JsonSerializer.Serialize(payload, typeInfo);
            var html = BuildHtmlDocument(title, json);
            return Results.Text(html, MediaTypes.Html);
        }

        var contentType = string.Equals(outputFormat, MediaTypes.GeoJson, StringComparison.OrdinalIgnoreCase)
            ? MediaTypes.GeoJson
            : MediaTypes.Json;

        return Results.Json(payload, typeInfo, contentType: contentType);
    }

    /// <summary>
    /// Builds an HTML document for browser-friendly feature display.
    /// </summary>
    public static string BuildHtmlDocument(string title, string json)
    {
        return $@"<!DOCTYPE html>
<html>
<head>
    <title>{title}</title>
    <style>
        body {{ font-family: Arial, sans-serif; margin: 40px; }}
        pre {{ background: #f5f5f5; padding: 20px; border-radius: 5px; overflow: auto; }}
        .title {{ color: #333; margin-bottom: 20px; }}
        .json-container {{
            max-height: 70vh;
            overflow-y: auto;
            border: 1px solid #ddd;
            border-radius: 5px;
        }}
        .metadata {{
            background: #e8f4f8;
            padding: 15px;
            margin-bottom: 20px;
            border-radius: 5px;
            border-left: 4px solid #007cba;
        }}
        .metadata h3 {{
            margin: 0 0 10px 0;
            color: #005577;
        }}
        .metadata p {{
            margin: 5px 0;
            font-size: 14px;
        }}
    </style>
</head>
<body>
    <h1 class=""title"">{title}</h1>
    <div class=""metadata"">
        <h3>OGC API Features Response</h3>
        <p><strong>Content Type:</strong> application/geo+json</p>
        <p><strong>Generated:</strong> {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC</p>
    </div>
    <div class=""json-container"">
        <pre><code>{json}</code></pre>
    </div>
</body>
</html>";
    }

    /// <summary>
    /// Builds GML representation of a feature collection.
    /// </summary>
    public static string BuildGmlFeatureCollection(IEnumerable<GeoJsonFeature> features)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"<wfs:FeatureCollection xmlns:wfs=\"{OgcFeaturesUtilities.WfsNamespace}\" xmlns:gml=\"{OgcFeaturesUtilities.GmlNamespace}\">");

        foreach (var feature in features)
        {
            builder.AppendLine("  <wfs:member>");
            builder.AppendLine($"    <gml:Feature gml:id=\"{feature.Id}\">");
            builder.AppendLine(BuildGmlGeometry(feature.Geometry, "      "));
            BuildGmlProperties(builder, feature.Properties, "      ");
            builder.AppendLine("    </gml:Feature>");
            builder.AppendLine("  </wfs:member>");
        }

        builder.AppendLine("</wfs:FeatureCollection>");
        return builder.ToString();
    }

    /// <summary>
    /// Builds GML representation of a single feature.
    /// </summary>
    public static string BuildGmlSingleFeature(GeoJsonFeature feature)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"<wfs:FeatureCollection xmlns:wfs=\"{OgcFeaturesUtilities.WfsNamespace}\" xmlns:gml=\"{OgcFeaturesUtilities.GmlNamespace}\">");
        builder.AppendLine("  <wfs:member>");
        builder.AppendLine($"    <gml:Feature gml:id=\"{feature.Id}\">");
        builder.AppendLine(BuildGmlGeometry(feature.Geometry, "      "));
        BuildGmlProperties(builder, feature.Properties, "      ");
        builder.AppendLine("    </gml:Feature>");
        builder.AppendLine("  </wfs:member>");
        builder.AppendLine("</wfs:FeatureCollection>");
        return builder.ToString();
    }

    /// <summary>
    /// Builds GML geometry representation from GeoJSON geometry.
    /// </summary>
    private static string BuildGmlGeometry(SimpleGeoJsonGeometry? geometry, string indent = "")
    {
        if (geometry?.CoordinatesJson == null)
        {
            return $"{indent}<gml:Point />";
        }

        return geometry.Type?.ToUpperInvariant() switch
        {
            "POINT" => BuildGmlPoint(geometry.CoordinatesJson, indent),
            "LINESTRING" => BuildGmlLineString(geometry.CoordinatesJson, indent),
            "POLYGON" => BuildGmlPolygon(geometry.CoordinatesJson, indent),
            "MULTIPOINT" => BuildGmlMultiPoint(geometry.CoordinatesJson, indent),
            "MULTILINESTRING" => BuildGmlMultiLineString(geometry.CoordinatesJson, indent),
            "MULTIPOLYGON" => BuildGmlMultiPolygon(geometry.CoordinatesJson, indent),
            _ => $"{indent}<gml:Geometry />"
        };
    }

    private static string BuildGmlPoint(string coordinatesJson, string indent)
    {
        try
        {
            using var document = JsonDocument.Parse(coordinatesJson);
            var coords = document.RootElement.EnumerateArray().ToArray();
            if (coords.Length >= 2)
            {
                var x = coords[0].GetDouble();
                var y = coords[1].GetDouble();
                return $"{indent}<gml:Point><gml:pos>{x} {y}</gml:pos></gml:Point>";
            }
        }
        catch (JsonException)
        {
            // Fall back to simple representation
        }

        return $"{indent}<gml:Point><gml:pos>{coordinatesJson}</gml:pos></gml:Point>";
    }

    private static string BuildGmlLineString(string coordinatesJson, string indent)
    {
        try
        {
            using var document = JsonDocument.Parse(coordinatesJson);
            var coordinates = new List<string>();
            foreach (var coord in document.RootElement.EnumerateArray())
            {
                var coordArray = coord.EnumerateArray().ToArray();
                if (coordArray.Length >= 2)
                {
                    var x = coordArray[0].GetDouble();
                    var y = coordArray[1].GetDouble();
                    coordinates.Add($"{x} {y}");
                }
            }

            if (coordinates.Count > 0)
            {
                var posListContent = string.Join(" ", coordinates);
                return $"{indent}<gml:LineString><gml:posList>{posListContent}</gml:posList></gml:LineString>";
            }
        }
        catch (JsonException)
        {
            // Fall back to simple representation
        }

        return $"{indent}<gml:LineString />";
    }

    private static string BuildGmlPolygon(string coordinatesJson, string indent)
    {
        try
        {
            using var document = JsonDocument.Parse(coordinatesJson);
            var rings = document.RootElement.EnumerateArray().ToArray();
            if (rings.Length > 0)
            {
                var builder = new StringBuilder();
                builder.AppendLine($"{indent}<gml:Polygon>");

                // Exterior ring
                var exteriorRing = rings[0];
                var coordinates = new List<string>();
                foreach (var coord in exteriorRing.EnumerateArray())
                {
                    var coordArray = coord.EnumerateArray().ToArray();
                    if (coordArray.Length >= 2)
                    {
                        var x = coordArray[0].GetDouble();
                        var y = coordArray[1].GetDouble();
                        coordinates.Add($"{x} {y}");
                    }
                }

                if (coordinates.Count > 0)
                {
                    var posListContent = string.Join(" ", coordinates);
                    builder.AppendLine($"{indent}  <gml:exterior>");
                    builder.AppendLine($"{indent}    <gml:LinearRing>");
                    builder.AppendLine($"{indent}      <gml:posList>{posListContent}</gml:posList>");
                    builder.AppendLine($"{indent}    </gml:LinearRing>");
                    builder.AppendLine($"{indent}  </gml:exterior>");
                }

                // Interior rings (holes)
                for (int i = 1; i < rings.Length; i++)
                {
                    var interiorRing = rings[i];
                    var interiorCoordinates = new List<string>();
                    foreach (var coord in interiorRing.EnumerateArray())
                    {
                        var coordArray = coord.EnumerateArray().ToArray();
                        if (coordArray.Length >= 2)
                        {
                            var x = coordArray[0].GetDouble();
                            var y = coordArray[1].GetDouble();
                            interiorCoordinates.Add($"{x} {y}");
                        }
                    }

                    if (interiorCoordinates.Count > 0)
                    {
                        var posListContent = string.Join(" ", interiorCoordinates);
                        builder.AppendLine($"{indent}  <gml:interior>");
                        builder.AppendLine($"{indent}    <gml:LinearRing>");
                        builder.AppendLine($"{indent}      <gml:posList>{posListContent}</gml:posList>");
                        builder.AppendLine($"{indent}    </gml:LinearRing>");
                        builder.AppendLine($"{indent}  </gml:interior>");
                    }
                }

                builder.Append($"{indent}</gml:Polygon>");
                return builder.ToString();
            }
        }
        catch (JsonException)
        {
            // Fall back to simple representation
        }

        return $"{indent}<gml:Polygon />";
    }

    private static string BuildGmlMultiPoint(string coordinatesJson, string indent)
    {
        return $"{indent}<gml:MultiPoint />";
    }

    private static string BuildGmlMultiLineString(string coordinatesJson, string indent)
    {
        return $"{indent}<gml:MultiLineString />";
    }

    private static string BuildGmlMultiPolygon(string coordinatesJson, string indent)
    {
        return $"{indent}<gml:MultiPolygon />";
    }

    private static void BuildGmlProperties(StringBuilder builder, Dictionary<string, object?>? properties, string indent)
    {
        if (properties == null || properties.Count == 0)
        {
            return;
        }

        foreach (var (key, value) in properties)
        {
            if (value == null)
            {
                continue;
            }

            var safeKey = key.Replace(" ", "_", StringComparison.Ordinal);
            var safeValue = value.ToString()?.Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal)
                .Replace("\"", "&quot;", StringComparison.Ordinal)
                .Replace("'", "&apos;", StringComparison.Ordinal);

            builder.AppendLine($"{indent}<gml:property name=\"{safeKey}\">{safeValue}</gml:property>");
        }
    }

    /// <summary>
    /// Builds CSV representation of features for data export.
    /// </summary>
    public static string BuildCsvResponse(IEnumerable<GeoJsonFeature> features, string[] fieldNames)
    {
        var builder = new StringBuilder();

        // Header
        var headers = new List<string> { "id" };
        headers.AddRange(fieldNames);
        headers.Add("geometry");
        builder.AppendLine(string.Join(",", headers.Select(EscapeCsvField)));

        // Data rows
        foreach (var feature in features)
        {
            var row = new List<string> { feature.Id?.ToString() ?? "" };

            foreach (var fieldName in fieldNames)
            {
                var value = feature.Properties?.TryGetValue(fieldName, out var fieldValue) == true
                    ? fieldValue?.ToString() ?? ""
                    : "";
                row.Add(value);
            }

            // Simplified geometry representation
            var geometryValue = feature.Geometry?.Type ?? "";
            row.Add(geometryValue);

            builder.AppendLine(string.Join(",", row.Select(EscapeCsvField)));
        }

        return builder.ToString();
    }

    private static string EscapeCsvField(string field)
    {
        if (string.IsNullOrEmpty(field))
        {
            return "";
        }

        if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
        {
            return $"\"{field.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }

        return field;
    }
}

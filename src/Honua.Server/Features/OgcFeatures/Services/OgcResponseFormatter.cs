// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.IO;
using System.Net;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.FeatureStore.Domain;
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
        var encodedTitle = WebUtility.HtmlEncode(title);
        var encodedJson = WebUtility.HtmlEncode(json);

        // Extract summary metadata from the JSON
        var metadata = ExtractFeatureMetadata(json);

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""utf-8"" />
    <meta name=""viewport"" content=""width=device-width, initial-scale=1"" />
    <title>{encodedTitle} - OGC API Features</title>
    <style>
        * {{ box-sizing: border-box; }}
        body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; margin: 0; padding: 20px 40px; color: #1a1a1a; background: #fafafa; }}
        .header {{ border-bottom: 2px solid #0077b6; padding-bottom: 12px; margin-bottom: 20px; }}
        .header h1 {{ margin: 0; color: #0077b6; font-size: 1.5em; }}
        .header .badge {{ display: inline-block; background: #0077b6; color: #fff; padding: 2px 8px; border-radius: 3px; font-size: 0.75em; margin-left: 8px; vertical-align: middle; }}
        .summary {{ display: flex; gap: 16px; flex-wrap: wrap; margin-bottom: 16px; }}
        .summary .stat {{ background: #fff; border: 1px solid #e0e0e0; border-radius: 6px; padding: 8px 16px; }}
        .summary .stat .label {{ font-size: 0.75em; color: #888; text-transform: uppercase; }}
        .summary .stat .value {{ font-size: 1.1em; font-weight: 600; }}
        nav {{ background: #fff; border: 1px solid #e0e0e0; border-radius: 6px; padding: 12px 16px; margin-bottom: 16px; }}
        nav h2 {{ margin: 0 0 8px 0; font-size: 0.9em; color: #555; text-transform: uppercase; letter-spacing: 0.5px; }}
        nav ul {{ list-style: none; padding: 0; margin: 0; display: flex; flex-wrap: wrap; gap: 8px; }}
        nav li a {{ display: inline-block; padding: 4px 12px; background: #e8f4f8; border-radius: 4px; text-decoration: none; color: #0077b6; font-size: 0.9em; }}
        nav li a:hover {{ background: #0077b6; color: #fff; }}
        .json-container {{ background: #fff; border: 1px solid #e0e0e0; border-radius: 6px; max-height: 70vh; overflow: auto; }}
        pre {{ margin: 0; padding: 16px; font-size: 0.85em; line-height: 1.5; white-space: pre-wrap; word-wrap: break-word; }}
    </style>
</head>
<body>
    <div class=""header"">
        <h1>{encodedTitle}<span class=""badge"">GeoJSON</span></h1>
    </div>
    {metadata}
    <div class=""json-container"">
        <pre><code>{encodedJson}</code></pre>
    </div>
</body>
</html>";
    }

    private static string ExtractFeatureMetadata(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var sb = new StringBuilder();

            // Summary statistics
            var hasStats = false;
            sb.Append("<div class=\"summary\">");

            if (root.TryGetProperty("type", out var typeProp))
            {
                var typeVal = typeProp.GetString();
                if (string.Equals(typeVal, "FeatureCollection", StringComparison.OrdinalIgnoreCase))
                {
                    if (root.TryGetProperty("numberMatched", out var matched))
                    {
                        sb.Append($"<div class=\"stat\"><div class=\"label\">Matched</div><div class=\"value\">{matched}</div></div>");
                        hasStats = true;
                    }

                    if (root.TryGetProperty("numberReturned", out var returned))
                    {
                        sb.Append($"<div class=\"stat\"><div class=\"label\">Returned</div><div class=\"value\">{returned}</div></div>");
                        hasStats = true;
                    }
                }
            }

            if (root.TryGetProperty("timeStamp", out var ts))
            {
                sb.Append($"<div class=\"stat\"><div class=\"label\">Timestamp</div><div class=\"value\">{WebUtility.HtmlEncode(ts.GetString())}</div></div>");
                hasStats = true;
            }

            sb.Append("</div>");
            if (!hasStats)
            {
                sb.Clear();
            }

            // Navigation links
            if (root.TryGetProperty("links", out var linksElement) &&
                linksElement.ValueKind == JsonValueKind.Array)
            {
                sb.Append("<nav><h2>Links</h2><ul>");
                foreach (var link in linksElement.EnumerateArray())
                {
                    var href = link.TryGetProperty("href", out var hrefProp) ? hrefProp.GetString() : null;
                    var rel = link.TryGetProperty("rel", out var relProp) ? relProp.GetString() : null;
                    var linkTitle = link.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;
                    if (string.IsNullOrEmpty(href))
                    {
                        continue;
                    }

                    var displayTitle = WebUtility.HtmlEncode(linkTitle ?? rel ?? href);
                    var htmlHref = href + (href.Contains('?') ? "&" : "?") + "f=html";
                    sb.Append($"<li><a href=\"{WebUtility.HtmlEncode(htmlHref)}\">{displayTitle}</a></li>");
                }

                sb.Append("</ul></nav>");
            }

            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Builds GML representation of a feature collection.
    /// </summary>
    public static string BuildGmlFeatureCollection(IEnumerable<GeoJsonFeature> features)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"<wfs:FeatureCollection xmlns:wfs=\"{OgcFeaturesUtilities.WfsNamespace}\" xmlns:gml=\"{OgcFeaturesUtilities.GmlNamespace}\" xmlns:app=\"{OgcFeaturesUtilities.AppNamespace}\">");

        foreach (var feature in features)
        {
            var escapedId = SecurityElement.Escape(feature.Id?.ToString() ?? string.Empty);
            builder.AppendLine("  <wfs:member>");
            builder.AppendLine($"    <app:Feature gml:id=\"{escapedId}\">");
            builder.AppendLine(BuildGmlGeometry(feature.Geometry, "      "));
            BuildGmlProperties(builder, feature.Properties, "      ");
            builder.AppendLine("    </app:Feature>");
            builder.AppendLine("  </wfs:member>");
        }

        builder.AppendLine("</wfs:FeatureCollection>");
        return builder.ToString();
    }

    /// <summary>
    /// Streams GML representation of a feature collection.
    /// </summary>
    public static async Task StreamGmlFeatureCollectionAsync(
        IAsyncEnumerable<GmlFeature> features,
        System.IO.Pipelines.PipeWriter bodyWriter,
        CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(
            bodyWriter.AsStream(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 8192,
            leaveOpen: true);

        await writer.WriteLineAsync(
            $"<wfs:FeatureCollection xmlns:wfs=\"{OgcFeaturesUtilities.WfsNamespace}\" xmlns:gml=\"{OgcFeaturesUtilities.GmlNamespace}\" xmlns:app=\"{OgcFeaturesUtilities.AppNamespace}\">");

        await foreach (var feature in features.WithCancellation(cancellationToken))
        {
            var escapedId = SecurityElement.Escape(feature.Id.ToString());
            await writer.WriteLineAsync("  <wfs:member>");
            await writer.WriteLineAsync($"    <app:Feature gml:id=\"{escapedId}\">");
            WriteGmlGeometry(writer, feature.GeometryGml, "      ");
            WriteGmlProperties(writer, feature.Attributes, "      ");
            await writer.WriteLineAsync("    </app:Feature>");
            await writer.WriteLineAsync("  </wfs:member>");
            await writer.FlushAsync(cancellationToken);
        }

        await writer.WriteLineAsync("</wfs:FeatureCollection>");
        await writer.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Builds GML representation of a single feature.
    /// </summary>
    public static string BuildGmlSingleFeature(GeoJsonFeature feature)
    {
        var builder = new StringBuilder();
        var escapedId = SecurityElement.Escape(feature.Id?.ToString());
        builder.AppendLine($"<wfs:FeatureCollection xmlns:wfs=\"{OgcFeaturesUtilities.WfsNamespace}\" xmlns:gml=\"{OgcFeaturesUtilities.GmlNamespace}\" xmlns:app=\"{OgcFeaturesUtilities.AppNamespace}\">");
        builder.AppendLine("  <wfs:member>");
        builder.AppendLine($"    <app:Feature gml:id=\"{escapedId}\">");
        builder.AppendLine(BuildGmlGeometry(feature.Geometry, "      "));
        BuildGmlProperties(builder, feature.Properties, "      ");
        builder.AppendLine("    </app:Feature>");
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
        try
        {
            using var document = JsonDocument.Parse(coordinatesJson);
            var points = document.RootElement.EnumerateArray().ToArray();
            if (points.Length > 0)
            {
                var builder = new StringBuilder();
                builder.AppendLine($"{indent}<gml:MultiPoint>");

                foreach (var point in points)
                {
                    var coords = point.EnumerateArray().ToArray();
                    if (coords.Length >= 2)
                    {
                        var x = coords[0].GetDouble();
                        var y = coords[1].GetDouble();
                        builder.AppendLine($"{indent}  <gml:pointMember>");
                        builder.AppendLine($"{indent}    <gml:Point><gml:pos>{x} {y}</gml:pos></gml:Point>");
                        builder.AppendLine($"{indent}  </gml:pointMember>");
                    }
                }

                builder.Append($"{indent}</gml:MultiPoint>");
                return builder.ToString();
            }
        }
        catch (JsonException)
        {
            // Fall back to simple representation
        }

        return $"{indent}<gml:MultiPoint />";
    }

    private static string BuildGmlMultiLineString(string coordinatesJson, string indent)
    {
        try
        {
            using var document = JsonDocument.Parse(coordinatesJson);
            var lineStrings = document.RootElement.EnumerateArray().ToArray();
            if (lineStrings.Length > 0)
            {
                var builder = new StringBuilder();
                builder.AppendLine($"{indent}<gml:MultiLineString>");

                foreach (var lineString in lineStrings)
                {
                    var coordinates = new List<string>();
                    foreach (var coord in lineString.EnumerateArray())
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
                        builder.AppendLine($"{indent}  <gml:lineStringMember>");
                        builder.AppendLine($"{indent}    <gml:LineString><gml:posList>{posListContent}</gml:posList></gml:LineString>");
                        builder.AppendLine($"{indent}  </gml:lineStringMember>");
                    }
                }

                builder.Append($"{indent}</gml:MultiLineString>");
                return builder.ToString();
            }
        }
        catch (JsonException)
        {
            // Fall back to simple representation
        }

        return $"{indent}<gml:MultiLineString />";
    }

    private static string BuildGmlMultiPolygon(string coordinatesJson, string indent)
    {
        try
        {
            using var document = JsonDocument.Parse(coordinatesJson);
            var polygons = document.RootElement.EnumerateArray().ToArray();
            if (polygons.Length > 0)
            {
                var builder = new StringBuilder();
                builder.AppendLine($"{indent}<gml:MultiPolygon>");

                foreach (var polygon in polygons)
                {
                    var rings = polygon.EnumerateArray().ToArray();
                    if (rings.Length > 0)
                    {
                        builder.AppendLine($"{indent}  <gml:polygonMember>");
                        builder.AppendLine($"{indent}    <gml:Polygon>");

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
                            builder.AppendLine($"{indent}      <gml:exterior>");
                            builder.AppendLine($"{indent}        <gml:LinearRing>");
                            builder.AppendLine($"{indent}          <gml:posList>{posListContent}</gml:posList>");
                            builder.AppendLine($"{indent}        </gml:LinearRing>");
                            builder.AppendLine($"{indent}      </gml:exterior>");
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
                                builder.AppendLine($"{indent}      <gml:interior>");
                                builder.AppendLine($"{indent}        <gml:LinearRing>");
                                builder.AppendLine($"{indent}          <gml:posList>{posListContent}</gml:posList>");
                                builder.AppendLine($"{indent}        </gml:LinearRing>");
                                builder.AppendLine($"{indent}      </gml:interior>");
                            }
                        }

                        builder.AppendLine($"{indent}    </gml:Polygon>");
                        builder.AppendLine($"{indent}  </gml:polygonMember>");
                    }
                }

                builder.Append($"{indent}</gml:MultiPolygon>");
                return builder.ToString();
            }
        }
        catch (JsonException)
        {
            // Fall back to simple representation
        }

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

            var safeKey = SecurityElement.Escape(key.Replace(" ", "_", StringComparison.Ordinal));
            var safeValue = SecurityElement.Escape(value.ToString());

            builder.AppendLine($"{indent}<app:property name=\"{safeKey}\">{safeValue}</app:property>");
        }
    }

    private static void WriteGmlGeometry(TextWriter writer, string? geometryGml, string indent)
    {
        if (string.IsNullOrWhiteSpace(geometryGml))
        {
            writer.WriteLine($"{indent}<gml:Point />");
            return;
        }

        var lines = geometryGml.Split('\n');
        foreach (var line in lines)
        {
            writer.Write(indent);
            writer.WriteLine(line.TrimEnd('\r'));
        }
    }

    private static void WriteGmlProperties(TextWriter writer, ImmutableDictionary<string, object?> properties, string indent)
    {
        if (properties.IsEmpty)
        {
            return;
        }

        foreach (var (key, value) in properties)
        {
            if (value == null)
            {
                continue;
            }

            var safeKey = SecurityElement.Escape(key.Replace(" ", "_", StringComparison.Ordinal));
            var safeValue = SecurityElement.Escape(value.ToString());

            writer.WriteLine($"{indent}<app:property name=\"{safeKey}\">{safeValue}</app:property>");
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

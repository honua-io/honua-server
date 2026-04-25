// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Xml.Linq;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Server.Features.Protocols.Ogc.Classic.Wfs20.Services;

/// <summary>
/// Converts between GML and domain feature formats for WFS 2.0 operations.
/// </summary>
internal sealed class Wfs20FeatureFormatConverter : IWfs20FeatureFormatConverter
{
    private static readonly XNamespace GmlNamespace = "http://www.opengis.net/gml/3.2";

    /// <inheritdoc />
    public Feature ConvertGmlToFeature(XElement gmlElement)
    {
        ArgumentNullException.ThrowIfNull(gmlElement);

        var id = ExtractFeatureId(gmlElement);
        var geometry = ExtractGeometry(gmlElement);
        var attributes = ExtractAttributes(gmlElement);

        // Convert GML geometry to WKB format if present
        byte[]? wkbGeometry = null;
        if (geometry != null)
        {
            // For production implementation, this would use NetTopologySuite
            // to parse GML and convert to WKB. For now, return null to maintain
            // compatibility with existing Feature creation patterns.
            wkbGeometry = null;
        }

        return Feature.Create(
            id: id,
            geometry: wkbGeometry,
            attributes: attributes.ToImmutableDictionary()
        );
    }

    /// <inheritdoc />
    public GmlFeature ConvertFeatureToGml(Feature feature)
    {
        // Convert WKB geometry to GML if present
        string? gmlGeometry = null;
        if (feature.Geometry != null)
        {
            gmlGeometry = ConvertWkbToGml(feature.Geometry);
        }

        return GmlFeature.Create(
            id: feature.Id,
            geometryGml: gmlGeometry,
            attributes: feature.Attributes
        );
    }

    /// <inheritdoc />
    public string? ExtractGeometry(XElement gmlElement)
    {
        ArgumentNullException.ThrowIfNull(gmlElement);

        try
        {
            // Look for geometry property elements
            var geometryProperty = gmlElement.Elements()
                .FirstOrDefault(e => e.Name.LocalName.EndsWith("geometry", StringComparison.OrdinalIgnoreCase) ||
                                    e.Name.Namespace == GmlNamespace);

            if (geometryProperty == null)
                return null;

            // Find the actual geometry element (child of the property)
            var geometryElement = geometryProperty.Elements()
                .FirstOrDefault(e => e.Name.Namespace == GmlNamespace);

            if (geometryElement == null)
                return null;

            // For a full implementation, this would parse the GML geometry to WKT
            // For now, return the GML as a string (this would need a proper GML parser)
            return geometryElement.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public Dictionary<string, object?> ExtractAttributes(XElement gmlElement)
    {
        ArgumentNullException.ThrowIfNull(gmlElement);

        var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in gmlElement.Elements())
        {
            // Skip GML namespace elements (geometry, id, etc.)
            if (element.Name.Namespace == GmlNamespace)
                continue;

            // Skip geometry property elements
            if (element.Name.LocalName.EndsWith("geometry", StringComparison.OrdinalIgnoreCase))
                continue;

            var name = element.Name.LocalName;
            var value = element.Value;

            // Store as string for simplicity - a full implementation would type the values
            attributes[name] = string.IsNullOrEmpty(value) ? null : value;
        }

        return attributes;
    }

    /// <inheritdoc />
    public object? ConvertAttributeValue(string value, Type expectedType)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        try
        {
            return expectedType switch
            {
                _ when expectedType == typeof(string) => value,
                _ when expectedType == typeof(int) || expectedType == typeof(int?) =>
                    int.Parse(value, CultureInfo.InvariantCulture),
                _ when expectedType == typeof(long) || expectedType == typeof(long?) =>
                    long.Parse(value, CultureInfo.InvariantCulture),
                _ when expectedType == typeof(double) || expectedType == typeof(double?) =>
                    double.Parse(value, CultureInfo.InvariantCulture),
                _ when expectedType == typeof(decimal) || expectedType == typeof(decimal?) =>
                    decimal.Parse(value, CultureInfo.InvariantCulture),
                _ when expectedType == typeof(bool) || expectedType == typeof(bool?) =>
                    bool.Parse(value),
                _ when expectedType == typeof(DateTime) || expectedType == typeof(DateTime?) =>
                    DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                _ when expectedType == typeof(Guid) || expectedType == typeof(Guid?) =>
                    Guid.Parse(value),
                _ => value // Fallback to string
            };
        }
        catch
        {
            return value; // Return as string if conversion fails
        }
    }

    private static long ExtractFeatureId(XElement gmlElement)
    {
        // Look for gml:id attribute
        var gmlId = gmlElement.Attribute(GmlNamespace + "id")?.Value;
        if (!string.IsNullOrEmpty(gmlId))
        {
            // Extract numeric ID from gml:id (e.g., "feature_123" -> 123)
            var match = System.Text.RegularExpressions.Regex.Match(gmlId, @"(\d+)");
            if (match.Success && long.TryParse(match.Value, out var id))
                return id;
        }

        // Fallback: look for an id element
        var idElement = gmlElement.Elements()
            .FirstOrDefault(e => e.Name.LocalName.Equals("id", StringComparison.OrdinalIgnoreCase));

        if (idElement != null && long.TryParse(idElement.Value, out var elementId))
            return elementId;

        // If no ID found, return 0 (would be auto-generated in a real implementation)
        return 0;
    }

    private static string? ConvertWkbToGml(byte[] wkb)
    {
        // This is a placeholder implementation
        // A full implementation would use NetTopologySuite to convert WKB to GML
        // For now, we'll create a simple GML Point as an example
        if (wkb.Length > 0)
        {
            // Very basic conversion for demonstration
            return $"<gml:Point xmlns:gml=\"{GmlNamespace}\"><gml:pos>0 0</gml:pos></gml:Point>";
        }

        return null;
    }
}

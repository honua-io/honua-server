// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Xml.Linq;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Server.Features.Protocols.Ogc.Classic.Wfs20.Services;

/// <summary>
/// Implementation of GML 3.2 serialization for WFS 2.0 responses.
/// </summary>
internal sealed class GmlSerializer : IGmlSerializer
{
    private static readonly XNamespace GmlNamespace = "http://www.opengis.net/gml/3.2";
    private static readonly XNamespace WfsNamespace = "http://www.opengis.net/wfs/2.0";

    /// <inheritdoc />
    public XElement SerializeFeature(GmlFeature feature, string featureTypeName, string namespaceUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureTypeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceUri);

        var featureNamespace = XNamespace.Get(namespaceUri);
        var featureElement = new XElement(featureNamespace + featureTypeName);

        // Add gml:id attribute
        featureElement.Add(new XAttribute(GmlNamespace + "id", $"feature_{feature.Id}"));

        // Add geometry if present
        var geometry = SerializeGeometry(feature.GeometryGml);
        if (geometry != null)
        {
            featureElement.Add(geometry);
        }

        // Add attributes
        foreach (var (key, value) in feature.Attributes)
        {
            if (value != null)
            {
                featureElement.Add(new XElement(featureNamespace + key, FormatAttributeValue(value)));
            }
        }

        return featureElement;
    }

    /// <inheritdoc />
    public XElement SerializeFeatureCollection(
        IEnumerable<GmlFeature> features,
        string featureTypeName,
        string namespaceUri,
        int? numberMatched = null,
        int? numberReturned = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureTypeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceUri);

        var collection = new XElement(WfsNamespace + "FeatureCollection");

        // Add namespace declarations
        collection.Add(new XAttribute(XNamespace.Xmlns + "wfs", WfsNamespace.NamespaceName));
        collection.Add(new XAttribute(XNamespace.Xmlns + "gml", GmlNamespace.NamespaceName));
        collection.Add(new XAttribute(XNamespace.Xmlns + "honua", namespaceUri));

        // Add metadata attributes
        if (numberMatched.HasValue)
        {
            collection.Add(new XAttribute("numberMatched", numberMatched.Value.ToString(CultureInfo.InvariantCulture)));
        }

        if (numberReturned.HasValue)
        {
            collection.Add(new XAttribute("numberReturned", numberReturned.Value.ToString(CultureInfo.InvariantCulture)));
        }

        collection.Add(new XAttribute("timeStamp", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)));

        // Add feature members
        foreach (var feature in features)
        {
            var member = new XElement(WfsNamespace + "member");
            member.Add(SerializeFeature(feature, featureTypeName, namespaceUri));
            collection.Add(member);
        }

        return collection;
    }

    /// <inheritdoc />
    public XElement? SerializeGeometry(string? geometryGml)
    {
        if (string.IsNullOrEmpty(geometryGml))
        {
            return null;
        }

        try
        {
            // Parse the GML fragment and wrap it in a geometry property element
            var gmlElement = XElement.Parse(geometryGml);
            var geometryProperty = new XElement(GmlNamespace + "geometryProperty");
            geometryProperty.Add(gmlElement);
            return geometryProperty;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Xml.XmlException)
        {
            // If GML parsing fails, return null (no geometry)
            return null;
        }
    }

    private static string FormatAttributeValue(object value)
    {
        return value switch
        {
            string s => s,
            DateTime dt => dt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            double db => db.ToString(CultureInfo.InvariantCulture),
            float f => f.ToString(CultureInfo.InvariantCulture),
            bool b => b.ToString().ToLowerInvariant(),
            null => string.Empty,
            _ => value.ToString() ?? string.Empty
        };
    }
}

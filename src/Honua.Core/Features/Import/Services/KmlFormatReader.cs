// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Core.Features.Import.Services;

/// <summary>
/// Streaming KML format reader. Parses KML Placemark elements into features
/// using XmlReader for memory-efficient processing.
/// </summary>
internal static class KmlFormatReader
{
    private static readonly char[] _coordinateSeparators = { ' ', '\n', '\r', '\t' };

    /// <summary>
    /// Streams features from a KML file using XmlReader for memory efficiency.
    /// </summary>
    internal static async IAsyncEnumerable<IFeature> ReadStreamingAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            IgnoreWhitespace = true,
            IgnoreComments = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };

        using var reader = XmlReader.Create(stream, settings);
        var geometryFactory = new GeometryFactory(new PrecisionModel(), 4326);

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Placemark")
            {
                var feature = await ParsePlacemarkAsync(reader, geometryFactory, cancellationToken);
                if (feature != null)
                    yield return feature;
            }
        }
    }

    private static async Task<IFeature?> ParsePlacemarkAsync(
        XmlReader reader,
        GeometryFactory geometryFactory,
        CancellationToken cancellationToken)
    {
        var attributes = new AttributesTable();
        NtsGeometry? geometry = null;

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "Placemark")
                break;

            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "name":
                        var name = await reader.ReadElementContentAsStringAsync();
                        AddOrReplaceAttribute(attributes, "name", name);
                        break;
                    case "description":
                        var desc = await reader.ReadElementContentAsStringAsync();
                        AddOrReplaceAttribute(attributes, "description", desc);
                        break;
                    case "ExtendedData":
                        await ParseExtendedDataAsync(reader, attributes, cancellationToken);
                        break;
                    case "Data":
                        await ParseDataElementAsync(reader, attributes, cancellationToken);
                        break;
                    case "SimpleData":
                        var simpleDataName = reader.GetAttribute("name");
                        var simpleDataValue = await reader.ReadElementContentAsStringAsync();
                        if (!string.IsNullOrWhiteSpace(simpleDataName))
                        {
                            AddOrReplaceAttribute(attributes, simpleDataName, simpleDataValue);
                        }
                        break;
                    case "Point":
                        geometry = await ParsePointAsync(reader, geometryFactory, cancellationToken);
                        break;
                    case "LineString":
                        geometry = await ParseLineStringAsync(reader, geometryFactory, cancellationToken);
                        break;
                    case "Polygon":
                        geometry = await ParsePolygonAsync(reader, geometryFactory, cancellationToken);
                        break;
                    case "MultiGeometry":
                        geometry = await ParseMultiGeometryAsync(reader, geometryFactory, cancellationToken);
                        break;
                }
            }
        }

        return new Feature(geometry, attributes);
    }

    private static async Task ParseExtendedDataAsync(
        XmlReader reader,
        AttributesTable attributes,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "ExtendedData")
                break;

            if (reader.NodeType != XmlNodeType.Element)
                continue;

            if (reader.LocalName == "Data")
            {
                await ParseDataElementAsync(reader, attributes, cancellationToken);
            }
            else if (reader.LocalName == "SimpleData")
            {
                var name = reader.GetAttribute("name");
                var value = await reader.ReadElementContentAsStringAsync();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    AddOrReplaceAttribute(attributes, name, value);
                }
            }
        }
    }

    private static async Task ParseDataElementAsync(
        XmlReader reader,
        AttributesTable attributes,
        CancellationToken cancellationToken)
    {
        var name = reader.GetAttribute("name");
        string? value = null;
        var dataDepth = reader.Depth;

        if (!reader.IsEmptyElement)
        {
            while (await reader.ReadAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (reader.NodeType == XmlNodeType.EndElement &&
                    reader.Depth == dataDepth &&
                    reader.LocalName == "Data")
                {
                    break;
                }

                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "value")
                {
                    value = await reader.ReadElementContentAsStringAsync();
                    if (reader.NodeType == XmlNodeType.EndElement &&
                        reader.Depth == dataDepth &&
                        reader.LocalName == "Data")
                    {
                        break;
                    }
                }
                else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA && value == null)
                {
                    value = reader.Value;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(name) && value != null)
        {
            AddOrReplaceAttribute(attributes, name, value);
        }
    }

    private static async Task<NtsGeometry?> ParsePointAsync(
        XmlReader reader,
        GeometryFactory factory,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "Point")
                break;

            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "coordinates")
            {
                var coords = await reader.ReadElementContentAsStringAsync();
                var coordinates = ParseCoordinates(coords);
                if (coordinates.Length > 0)
                {
                    return factory.CreatePoint(coordinates[0]);
                }
            }
        }
        return null;
    }

    private static async Task<NtsGeometry?> ParseLineStringAsync(
        XmlReader reader,
        GeometryFactory factory,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "LineString")
                break;

            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "coordinates")
            {
                var coords = await reader.ReadElementContentAsStringAsync();
                var coordinates = ParseCoordinates(coords);
                if (coordinates.Length >= 2)
                    return factory.CreateLineString(coordinates);
            }
        }
        return null;
    }

    private static async Task<NtsGeometry?> ParseMultiGeometryAsync(
        XmlReader reader,
        GeometryFactory factory,
        CancellationToken cancellationToken)
    {
        var geometries = new List<NtsGeometry>();

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "MultiGeometry")
                break;

            if (reader.NodeType != XmlNodeType.Element)
                continue;

            NtsGeometry? geometry = reader.LocalName switch
            {
                "Point" => await ParsePointAsync(reader, factory, cancellationToken),
                "LineString" => await ParseLineStringAsync(reader, factory, cancellationToken),
                "Polygon" => await ParsePolygonAsync(reader, factory, cancellationToken),
                "MultiGeometry" => await ParseMultiGeometryAsync(reader, factory, cancellationToken),
                _ => null
            };

            if (geometry != null)
            {
                geometries.Add(geometry);
            }
        }

        return geometries.Count == 0 ? null : factory.BuildGeometry(geometries);
    }

    private static async Task<NtsGeometry?> ParsePolygonAsync(
        XmlReader reader,
        GeometryFactory factory,
        CancellationToken cancellationToken)
    {
        LinearRing? outerRing = null;
        var innerRings = new List<LinearRing>();

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "Polygon")
                break;

            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.LocalName == "outerBoundaryIs")
                {
                    outerRing = await ParseBoundaryAsync(reader, factory, "outerBoundaryIs", cancellationToken);
                }
                else if (reader.LocalName == "innerBoundaryIs")
                {
                    var ring = await ParseBoundaryAsync(reader, factory, "innerBoundaryIs", cancellationToken);
                    if (ring != null)
                        innerRings.Add(ring);
                }
            }
        }

        if (outerRing != null)
            return factory.CreatePolygon(outerRing, innerRings.ToArray());

        return null;
    }

    private static async Task<LinearRing?> ParseBoundaryAsync(
        XmlReader reader,
        GeometryFactory factory,
        string boundaryName,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == boundaryName)
                break;

            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "coordinates")
            {
                var coords = await reader.ReadElementContentAsStringAsync();
                var coordinates = ParseCoordinates(coords);
                if (coordinates.Length >= 4)
                    return factory.CreateLinearRing(coordinates);
            }
        }
        return null;
    }

    private static Coordinate[] ParseCoordinates(string coordsString)
    {
        var coords = new List<Coordinate>();
        var parts = coordsString.Trim().Split(_coordinateSeparators, StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            var components = part.Split(',');
            if (components.Length >= 2 &&
                double.TryParse(components[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) &&
                double.TryParse(components[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat))
            {
                if (components.Length >= 3 &&
                    double.TryParse(components[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var altitude))
                {
                    coords.Add(new CoordinateZ(lon, lat, altitude));
                }
                else
                {
                    coords.Add(new Coordinate(lon, lat));
                }
            }
        }

        return coords.ToArray();
    }

    private static void AddOrReplaceAttribute(AttributesTable attributes, string name, object? value)
    {
        if (attributes.Exists(name))
        {
            attributes.DeleteAttribute(name);
        }

        attributes.Add(name, value);
    }
}

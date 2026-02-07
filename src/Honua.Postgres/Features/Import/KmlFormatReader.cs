// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Postgres.Features.Import;

/// <summary>
/// Streaming KML format reader. Parses KML Placemark elements into features
/// using XmlReader for memory-efficient processing.
/// </summary>
internal static class KmlFormatReader
{
    private static readonly char[] CoordinateSeparators = { ' ', '\n', '\r', '\t' };

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
                        attributes.Add("name", name);
                        break;
                    case "description":
                        var desc = await reader.ReadElementContentAsStringAsync();
                        attributes.Add("description", desc);
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
                }
            }
        }

        return new Feature(geometry, attributes);
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
                var parts = coords.Trim().Split(',');
                if (parts.Length >= 2 &&
                    double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) &&
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat))
                {
                    return factory.CreatePoint(new Coordinate(lon, lat));
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
        var parts = coordsString.Trim().Split(CoordinateSeparators, StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            var components = part.Split(',');
            if (components.Length >= 2 &&
                double.TryParse(components[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) &&
                double.TryParse(components[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat))
            {
                coords.Add(new Coordinate(lon, lat));
            }
        }

        return coords.ToArray();
    }
}

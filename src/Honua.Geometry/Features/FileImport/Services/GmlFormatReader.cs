// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.FileImport.Services;

/// <summary>
/// Streaming GML (Geography Markup Language) format reader.
/// Parses GML 2.x and GML 3.x feature collections into NTS features
/// using XmlReader for memory-efficient processing.
/// Supports common simple-feature geometry types: Point, LineString, Polygon,
/// MultiPoint, MultiLineString, MultiPolygon, MultiSurface, MultiCurve, and
/// MultiGeometry. Properties are read from non-geometry child elements.
/// </summary>
internal static class GmlFormatReader
{
    /// <summary>
    /// GML 3 namespace URI.
    /// </summary>
    internal const string GmlNs3 = "http://www.opengis.net/gml";

    /// <summary>
    /// GML 3.2 namespace URI.
    /// </summary>
    internal const string GmlNs32 = "http://www.opengis.net/gml/3.2";

    private static readonly char[] _coordinateSeparators = { ' ', '\n', '\r', '\t' };

    /// <summary>
    /// Tries to detect the CRS SRS name embedded in the top-level FeatureCollection element.
    /// Returns <see langword="null"/> if no srsName is found or it cannot be parsed to an EPSG code.
    /// </summary>
    internal static async Task<int?> TryDetectSridAsync(Stream stream, CancellationToken cancellationToken)
    {
        var settings = BuildXmlReaderSettings();
        using var reader = XmlReader.Create(stream, settings);

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.Element)
            {
                var srsName = reader.GetAttribute("srsName");
                if (!string.IsNullOrWhiteSpace(srsName))
                {
                    return ParseSrsNameToSrid(srsName);
                }

                // Only scan the root element — stop at the first feature member
                var localName = reader.LocalName;
                if (localName is "featureMember" or "member" or "featureMembers")
                {
                    break;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Streams features from a GML file using XmlReader for memory efficiency.
    /// Recognises <c>featureMember</c> / <c>member</c> / <c>featureMembers</c>
    /// wrapper patterns from both GML 2 and GML 3/3.2.
    /// </summary>
    internal static async IAsyncEnumerable<IFeature> ReadStreamingAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var settings = BuildXmlReaderSettings();
        using var reader = XmlReader.Create(stream, settings);
        var geometryFactory = GeometryFactory.Floating;

        // Detect the global srsName on the FeatureCollection element so features without an
        // explicit per-geometry srsName get the right default.
        int? collectionSrid = null;

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            var ns = reader.NamespaceURI;
            var localName = reader.LocalName;

            // Capture the srsName from the root collection element
            if (collectionSrid == null && (localName is "FeatureCollection" or "SimpleFeatureCollection"))
            {
                var srsName = reader.GetAttribute("srsName");
                if (!string.IsNullOrWhiteSpace(srsName))
                {
                    collectionSrid = ParseSrsNameToSrid(srsName);
                }
            }

            // Enter a featureMember / member / featureMembers wrapper
            if (localName is "featureMember" or "member" or "featureMembers")
            {
                await foreach (var feature in ReadFeatureMemberAsync(reader, geometryFactory, collectionSrid, cancellationToken))
                {
                    yield return feature;
                }
            }
        }
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private static async IAsyncEnumerable<IFeature> ReadFeatureMemberAsync(
        XmlReader reader,
        GeometryFactory factory,
        int? collectionSrid,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var wrapperLocalName = reader.LocalName;
        var wrapperDepth = reader.Depth;

        // Advance into the wrapper element
        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement &&
                reader.LocalName == wrapperLocalName &&
                reader.Depth == wrapperDepth)
            {
                yield break;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            // The first child element is the application-schema feature type
            // (e.g. <sf:SomeFeature>, <gml:boundedBy>, etc.) Skip gml:boundedBy
            if (reader.LocalName == "boundedBy")
            {
                await reader.SkipAsync();
                continue;
            }

            var feature = await ParseFeatureElementAsync(reader, factory, collectionSrid, cancellationToken);
            if (feature != null)
            {
                yield return feature;
            }
        }
    }

    private static async Task<IFeature?> ParseFeatureElementAsync(
        XmlReader reader,
        GeometryFactory factory,
        int? collectionSrid,
        CancellationToken cancellationToken)
    {
        var featureLocalName = reader.LocalName;
        var featureDepth = reader.Depth;
        var attributes = new AttributesTable();
        NtsGeometry? geometry = null;

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement &&
                reader.LocalName == featureLocalName &&
                reader.Depth == featureDepth)
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            var childNs = reader.NamespaceURI;
            var childLocal = reader.LocalName;

            // GML geometry elements are in the GML namespace
            if (IsGmlNamespace(childNs))
            {
                var geom = await TryParseGeometryAsync(reader, factory, collectionSrid, cancellationToken);
                if (geom != null)
                {
                    geometry = geom;
                    continue;
                }
            }

            // Property wrapper: the child of a non-GML property element may be a geometry
            if (childLocal == "boundedBy")
            {
                await reader.SkipAsync();
                continue;
            }

            // Try to read property value — may be a geometry or a text value
            var propertyLocalName = childLocal;
            var propertyDepth = reader.Depth;
            var isEmptyElement = reader.IsEmptyElement;

            if (isEmptyElement)
            {
                AddAttribute(attributes, propertyLocalName, string.Empty);
                continue;
            }

            // Peek inside the property element
            NtsGeometry? propertyGeom = null;
            string? propertyText = null;

            while (await reader.ReadAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (reader.NodeType == XmlNodeType.EndElement &&
                    reader.LocalName == propertyLocalName &&
                    reader.Depth == propertyDepth)
                {
                    break;
                }

                if (reader.NodeType == XmlNodeType.Text || reader.NodeType == XmlNodeType.CDATA)
                {
                    propertyText = (propertyText ?? string.Empty) + reader.Value;
                    continue;
                }

                if (reader.NodeType == XmlNodeType.Element && IsGmlNamespace(reader.NamespaceURI))
                {
                    var geom = await TryParseGeometryAsync(reader, factory, collectionSrid, cancellationToken);
                    if (geom != null)
                    {
                        propertyGeom = geom;
                        continue;
                    }
                }
            }

            if (propertyGeom != null)
            {
                geometry = propertyGeom;
            }
            else if (propertyText != null)
            {
                AddAttribute(attributes, propertyLocalName, propertyText);
            }
        }

        if (geometry == null && attributes.Count == 0)
        {
            return null;
        }

        return new Feature(geometry, attributes);
    }

    private static async Task<NtsGeometry?> TryParseGeometryAsync(
        XmlReader reader,
        GeometryFactory factory,
        int? collectionSrid,
        CancellationToken cancellationToken)
    {
        return reader.LocalName switch
        {
            "Point" => await ParsePointAsync(reader, factory, collectionSrid, cancellationToken),
            "LineString" or "Curve" => await ParseLineStringAsync(reader, factory, collectionSrid, cancellationToken),
            "LinearRing" => await ParseLinearRingAsync(reader, factory, collectionSrid, cancellationToken),
            "Polygon" or "Surface" => await ParsePolygonAsync(reader, factory, collectionSrid, cancellationToken),
            "MultiPoint" => await ParseMultiPointAsync(reader, factory, collectionSrid, cancellationToken),
            "MultiLineString" or "MultiCurve" => await ParseMultiLineStringAsync(reader, factory, collectionSrid, cancellationToken),
            "MultiPolygon" or "MultiSurface" => await ParseMultiPolygonAsync(reader, factory, collectionSrid, cancellationToken),
            "MultiGeometry" or "GeometryCollection" => await ParseGeometryCollectionAsync(reader, factory, collectionSrid, cancellationToken),
            _ => null
        };
    }

    // -----------------------------------------------------------------------
    // Geometry parsers
    // -----------------------------------------------------------------------

    private static async Task<Point?> ParsePointAsync(
        XmlReader reader,
        GeometryFactory factory,
        int? collectionSrid,
        CancellationToken cancellationToken)
    {
        var elementLocalName = reader.LocalName;
        var depth = reader.Depth;
        int? srid = ParseSrsNameToSrid(reader.GetAttribute("srsName")) ?? collectionSrid;

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement &&
                reader.LocalName == elementLocalName &&
                reader.Depth == depth)
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.LocalName is "pos" or "coordinates")
            {
                var coords = await reader.ReadElementContentAsStringAsync();
                var coordinate = ParsePos(coords);
                if (coordinate != null)
                {
                    var point = factory.CreatePoint(coordinate);
                    if (srid.HasValue)
                    {
                        point.SRID = srid.Value;
                    }

                    return point;
                }
            }
        }

        return null;
    }

    private static async Task<LineString?> ParseLineStringAsync(
        XmlReader reader,
        GeometryFactory factory,
        int? collectionSrid,
        CancellationToken cancellationToken)
    {
        var elementLocalName = reader.LocalName;
        var depth = reader.Depth;
        int? srid = ParseSrsNameToSrid(reader.GetAttribute("srsName")) ?? collectionSrid;

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement &&
                reader.LocalName == elementLocalName &&
                reader.Depth == depth)
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.LocalName is "posList" or "coordinates")
            {
                var coords = await reader.ReadElementContentAsStringAsync();
                var coordinates = ParsePosList(coords);
                if (coordinates.Length >= 2)
                {
                    var line = factory.CreateLineString(coordinates);
                    if (srid.HasValue)
                    {
                        line.SRID = srid.Value;
                    }

                    return line;
                }
            }
        }

        return null;
    }

    private static async Task<LinearRing?> ParseLinearRingAsync(
        XmlReader reader,
        GeometryFactory factory,
        int? collectionSrid,
        CancellationToken cancellationToken)
    {
        var elementLocalName = reader.LocalName;
        var depth = reader.Depth;
        int? srid = ParseSrsNameToSrid(reader.GetAttribute("srsName")) ?? collectionSrid;

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement &&
                reader.LocalName == elementLocalName &&
                reader.Depth == depth)
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.LocalName is "posList" or "coordinates")
            {
                var coords = await reader.ReadElementContentAsStringAsync();
                var coordinates = ParsePosList(coords);
                if (coordinates.Length >= 4)
                {
                    var ring = factory.CreateLinearRing(coordinates);
                    if (srid.HasValue)
                    {
                        ring.SRID = srid.Value;
                    }

                    return ring;
                }
            }
        }

        return null;
    }

    private static async Task<Polygon?> ParsePolygonAsync(
        XmlReader reader,
        GeometryFactory factory,
        int? collectionSrid,
        CancellationToken cancellationToken)
    {
        var elementLocalName = reader.LocalName;
        var depth = reader.Depth;
        int? srid = ParseSrsNameToSrid(reader.GetAttribute("srsName")) ?? collectionSrid;
        LinearRing? outerRing = null;
        var innerRings = new List<LinearRing>();

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement &&
                reader.LocalName == elementLocalName &&
                reader.Depth == depth)
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            // GML 2: <outerBoundaryIs>/<innerBoundaryIs> containing <LinearRing>
            // GML 3: <exterior>/<interior> containing <LinearRing>
            if (reader.LocalName is "outerBoundaryIs" or "exterior")
            {
                var ring = await ParseRingMemberAsync(reader, factory, srid ?? collectionSrid, cancellationToken);
                if (ring != null)
                {
                    outerRing = ring;
                }
            }
            else if (reader.LocalName is "innerBoundaryIs" or "interior")
            {
                var ring = await ParseRingMemberAsync(reader, factory, srid ?? collectionSrid, cancellationToken);
                if (ring != null)
                {
                    innerRings.Add(ring);
                }
            }
            else if (reader.LocalName == "LinearRing")
            {
                // GML 2 may nest LinearRing directly under Polygon
                var ring = await ParseLinearRingAsync(reader, factory, srid ?? collectionSrid, cancellationToken);
                if (ring != null && outerRing == null)
                {
                    outerRing = ring;
                }
            }
        }

        if (outerRing == null)
        {
            return null;
        }

        var polygon = factory.CreatePolygon(outerRing, innerRings.Count > 0 ? innerRings.ToArray() : null);
        if (srid.HasValue)
        {
            polygon.SRID = srid.Value;
        }

        return polygon;
    }

    private static async Task<LinearRing?> ParseRingMemberAsync(
        XmlReader reader,
        GeometryFactory factory,
        int? srid,
        CancellationToken cancellationToken)
    {
        var memberLocalName = reader.LocalName;
        var depth = reader.Depth;

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement &&
                reader.LocalName == memberLocalName &&
                reader.Depth == depth)
            {
                break;
            }

            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "LinearRing")
            {
                return await ParseLinearRingAsync(reader, factory, srid, cancellationToken);
            }
        }

        return null;
    }

    private static async Task<MultiPoint?> ParseMultiPointAsync(
        XmlReader reader,
        GeometryFactory factory,
        int? collectionSrid,
        CancellationToken cancellationToken)
    {
        var elementLocalName = reader.LocalName;
        var depth = reader.Depth;
        int? srid = ParseSrsNameToSrid(reader.GetAttribute("srsName")) ?? collectionSrid;
        var points = new List<Point>();

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement &&
                reader.LocalName == elementLocalName &&
                reader.Depth == depth)
            {
                break;
            }

            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Point")
            {
                var point = await ParsePointAsync(reader, factory, srid ?? collectionSrid, cancellationToken);
                if (point != null)
                {
                    points.Add(point);
                }
            }
        }

        if (points.Count == 0)
        {
            return null;
        }

        var multiPoint = factory.CreateMultiPoint(points.ToArray());
        if (srid.HasValue)
        {
            multiPoint.SRID = srid.Value;
        }

        return multiPoint;
    }

    private static async Task<MultiLineString?> ParseMultiLineStringAsync(
        XmlReader reader,
        GeometryFactory factory,
        int? collectionSrid,
        CancellationToken cancellationToken)
    {
        var elementLocalName = reader.LocalName;
        var depth = reader.Depth;
        int? srid = ParseSrsNameToSrid(reader.GetAttribute("srsName")) ?? collectionSrid;
        var lines = new List<LineString>();

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement &&
                reader.LocalName == elementLocalName &&
                reader.Depth == depth)
            {
                break;
            }

            if (reader.NodeType == XmlNodeType.Element &&
                reader.LocalName is "LineString" or "Curve" or "lineStringMember" or "curveMember")
            {
                if (reader.LocalName is "lineStringMember" or "curveMember")
                {
                    // wrapper element — advance to the actual geometry inside
                    continue;
                }

                var line = await ParseLineStringAsync(reader, factory, srid ?? collectionSrid, cancellationToken);
                if (line != null)
                {
                    lines.Add(line);
                }
            }
        }

        if (lines.Count == 0)
        {
            return null;
        }

        var multiLine = factory.CreateMultiLineString(lines.ToArray());
        if (srid.HasValue)
        {
            multiLine.SRID = srid.Value;
        }

        return multiLine;
    }

    private static async Task<MultiPolygon?> ParseMultiPolygonAsync(
        XmlReader reader,
        GeometryFactory factory,
        int? collectionSrid,
        CancellationToken cancellationToken)
    {
        var elementLocalName = reader.LocalName;
        var depth = reader.Depth;
        int? srid = ParseSrsNameToSrid(reader.GetAttribute("srsName")) ?? collectionSrid;
        var polygons = new List<Polygon>();

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement &&
                reader.LocalName == elementLocalName &&
                reader.Depth == depth)
            {
                break;
            }

            if (reader.NodeType == XmlNodeType.Element &&
                reader.LocalName is "Polygon" or "Surface" or "polygonMember" or "surfaceMember")
            {
                if (reader.LocalName is "polygonMember" or "surfaceMember")
                {
                    // wrapper element — advance to the actual geometry inside
                    continue;
                }

                var polygon = await ParsePolygonAsync(reader, factory, srid ?? collectionSrid, cancellationToken);
                if (polygon != null)
                {
                    polygons.Add(polygon);
                }
            }
        }

        if (polygons.Count == 0)
        {
            return null;
        }

        var multiPolygon = factory.CreateMultiPolygon(polygons.ToArray());
        if (srid.HasValue)
        {
            multiPolygon.SRID = srid.Value;
        }

        return multiPolygon;
    }

    private static async Task<GeometryCollection?> ParseGeometryCollectionAsync(
        XmlReader reader,
        GeometryFactory factory,
        int? collectionSrid,
        CancellationToken cancellationToken)
    {
        var elementLocalName = reader.LocalName;
        var depth = reader.Depth;
        int? srid = ParseSrsNameToSrid(reader.GetAttribute("srsName")) ?? collectionSrid;
        var geometries = new List<NtsGeometry>();

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement &&
                reader.LocalName == elementLocalName &&
                reader.Depth == depth)
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (!IsGmlNamespace(reader.NamespaceURI) &&
                reader.LocalName is not ("Point" or "LineString" or "Curve" or "Polygon" or "Surface"
                    or "MultiPoint" or "MultiLineString" or "MultiCurve" or "MultiPolygon" or "MultiSurface"
                    or "LinearRing"))
            {
                continue;
            }

            var geom = await TryParseGeometryAsync(reader, factory, srid ?? collectionSrid, cancellationToken);
            if (geom != null)
            {
                geometries.Add(geom);
            }
        }

        if (geometries.Count == 0)
        {
            return null;
        }

        var collection = factory.CreateGeometryCollection(geometries.ToArray());
        if (srid.HasValue)
        {
            collection.SRID = srid.Value;
        }

        return collection;
    }

    // -----------------------------------------------------------------------
    // Coordinate parsers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Parses a GML <c>pos</c> element value (a single position).
    /// Supports 2D and 3D coordinates in axis-order as written (typically lon/lat or lat/lon
    /// depending on the producing system). The coordinate order is passed through as-is;
    /// reprojection happens in the import pipeline.
    /// </summary>
    private static Coordinate? ParsePos(string posText)
    {
        var parts = posText.Trim().Split(_coordinateSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
        {
            return null;
        }

        if (parts.Length >= 3 &&
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        {
            return new CoordinateZ(x, y, z);
        }

        return new Coordinate(x, y);
    }

    /// <summary>
    /// Parses a GML <c>posList</c> or GML 2 <c>coordinates</c> element value
    /// into an array of coordinates.
    /// <para>
    /// GML 3 <c>posList</c>: space-separated flat ordinate list with implicit srsDimension
    /// (defaults to 2). Values assumed to be grouped as x y [z] x y [z] …
    /// </para>
    /// <para>
    /// GML 2 <c>coordinates</c>: comma-delimited tuples separated by space/newline
    /// in the form "x,y[,z] x,y[,z] …".
    /// </para>
    /// </summary>
    private static Coordinate[] ParsePosList(string text)
    {
        var trimmed = text.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return [];
        }

        // GML 2 coordinates element uses comma-delimited tuples
        if (trimmed.Contains(','))
        {
            return ParseGml2Coordinates(trimmed);
        }

        // GML 3 posList: flat space-separated ordinates
        return ParseGml3PosList(trimmed);
    }

    private static Coordinate[] ParseGml2Coordinates(string text)
    {
        var coords = new List<Coordinate>();
        var tuples = text.Split(_coordinateSeparators, StringSplitOptions.RemoveEmptyEntries);

        foreach (var tuple in tuples)
        {
            var parts = tuple.Split(',');
            if (parts.Length < 2)
            {
                continue;
            }

            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                continue;
            }

            if (parts.Length >= 3 &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            {
                coords.Add(new CoordinateZ(x, y, z));
            }
            else
            {
                coords.Add(new Coordinate(x, y));
            }
        }

        return coords.ToArray();
    }

    private static Coordinate[] ParseGml3PosList(string text)
    {
        var parts = text.Split(_coordinateSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
        {
            // Not enough ordinates for even a 2D pair; treat as 2D regardless
        }

        var coords = new List<Coordinate>();

        // Determine if likely 3D: if the total count is divisible by 3 but not by 2,
        // treat as 3D. Otherwise treat as 2D (most common).
        var is3D = parts.Length >= 3 && parts.Length % 3 == 0 && parts.Length % 2 != 0;

        if (is3D)
        {
            for (var i = 0; i + 2 < parts.Length; i += 3)
            {
                if (double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                    double.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
                    double.TryParse(parts[i + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                {
                    coords.Add(new CoordinateZ(x, y, z));
                }
            }
        }
        else
        {
            for (var i = 0; i + 1 < parts.Length; i += 2)
            {
                if (double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                    double.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                {
                    coords.Add(new Coordinate(x, y));
                }
            }
        }

        return coords.ToArray();
    }

    // -----------------------------------------------------------------------
    // SRS / namespace helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Parses an SRS name attribute (e.g. <c>urn:ogc:def:crs:EPSG::4326</c>,
    /// <c>EPSG:4326</c>, <c>http://www.opengis.net/gml/srs/epsg.xml#4326</c>)
    /// into an integer EPSG SRID.
    /// </summary>
    internal static int? ParseSrsNameToSrid(string? srsName)
    {
        if (string.IsNullOrWhiteSpace(srsName))
        {
            return null;
        }

        // "EPSG:4326"
        var colonIdx = srsName.LastIndexOf(':');
        if (colonIdx >= 0 && int.TryParse(srsName.AsSpan(colonIdx + 1), out var srid) && srid > 0)
        {
            return srid;
        }

        // "http://www.opengis.net/gml/srs/epsg.xml#4326"
        var hashIdx = srsName.LastIndexOf('#');
        if (hashIdx >= 0 && int.TryParse(srsName.AsSpan(hashIdx + 1), out var sridHash) && sridHash > 0)
        {
            return sridHash;
        }

        return null;
    }

    private static bool IsGmlNamespace(string? ns)
        => ns is GmlNs3 or GmlNs32;

    private static void AddAttribute(AttributesTable attributes, string name, object? value)
    {
        if (!attributes.Exists(name))
        {
            attributes.Add(name, value);
        }
        else
        {
            attributes.DeleteAttribute(name);
            attributes.Add(name, value);
        }
    }

    private static XmlReaderSettings BuildXmlReaderSettings()
        => new()
        {
            Async = true,
            IgnoreWhitespace = true,
            IgnoreComments = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };
}

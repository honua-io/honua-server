// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Import.Domain;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.FileImport.Services;

internal static class GmlFormatReader
{
    internal const string GmlNs3 = "http://www.opengis.net/gml";

    internal const string GmlNs32 = "http://www.opengis.net/gml/3.2";

    private static readonly char[] _coordinateSeparators = { ' ', '\n', '\r', '\t' };

    private readonly record struct GmlSrs(int? Srid, bool SwapAxes)
    {
        public static readonly GmlSrs None = new(null, false);
    }

    private static GmlSrs ResolveElementSrs(string? srsName, GmlSrs inherited)
    {
        if (string.IsNullOrWhiteSpace(srsName))
        {
            return inherited;
        }

        var srid = ParseSrsNameToSrid(srsName);
        if (srid is null)
        {
            return inherited;
        }

        return new GmlSrs(srid, RequiresAxisSwap(srsName, srid.Value));
    }

    private static bool RequiresAxisSwap(string srsName, int srid)
        => UsesAuthorityAxisOrder(srsName) && GeographicSridClassifier.IsGeographicSrid(srid);

    private static bool UsesAuthorityAxisOrder(string srsName)
    {
        var s = srsName.Trim();

        // CRS84 (in any spelling) is explicitly long/lat regardless of its URN wrapper.
        if (s.EndsWith("CRS84", StringComparison.OrdinalIgnoreCase)
            || s.EndsWith("CRS:84", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return s.StartsWith("urn:", StringComparison.OrdinalIgnoreCase)
            || s.Contains("/def/crs/", StringComparison.OrdinalIgnoreCase);
    }

    private static void SwapXy(Coordinate coordinate)
        => (coordinate.X, coordinate.Y) = (coordinate.Y, coordinate.X);

    private static void SwapXy(Coordinate[] coordinates)
    {
        foreach (var coordinate in coordinates)
        {
            SwapXy(coordinate);
        }
    }

    internal static async Task<int?> TryDetectSridAsync(Stream stream, CancellationToken cancellationToken)
        => (await InspectCoordinateSystemAsync(stream, cancellationToken)).DetectedSrid;

    internal static async Task<ImportCoordinateSystem> InspectCoordinateSystemAsync(Stream stream, CancellationToken cancellationToken)
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
                    return ImportCoordinateSystem.FromDetection(ParseSrsNameToSrid(srsName), ImportCrsProvenance.FileMetadata);
                }

                // Only scan the root element — stop at the first feature member
                var localName = reader.LocalName;
                if (localName is "featureMember" or "member" or "featureMembers")
                {
                    break;
                }
            }
        }

        return ImportCoordinateSystem.FromDetection(null, ImportCrsProvenance.Unknown);
    }

    internal static async IAsyncEnumerable<IFeature> ReadStreamingAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var settings = BuildXmlReaderSettings();
        using var reader = XmlReader.Create(stream, settings);
        var geometryFactory = GeometryFactory.Floating;

        // Detect the global srsName on the FeatureCollection element so features without an
        // explicit per-geometry srsName get the right default CRS and axis order.
        var collectionSrs = GmlSrs.None;
        var collectionSrsResolved = false;

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            var localName = reader.LocalName;

            // Capture the srsName from the root collection element
            if (!collectionSrsResolved && (localName is "FeatureCollection" or "SimpleFeatureCollection"))
            {
                var srsName = reader.GetAttribute("srsName");
                if (!string.IsNullOrWhiteSpace(srsName))
                {
                    collectionSrs = ResolveElementSrs(srsName, GmlSrs.None);
                    // Match the original guard: keep looking until a usable EPSG code is found, so a
                    // first collection element carrying an unparseable srsName does not lock in None.
                    collectionSrsResolved = collectionSrs.Srid is not null;
                }
            }

            // Enter a featureMember / member / featureMembers wrapper
            if (localName is "featureMember" or "member" or "featureMembers")
            {
                await foreach (var feature in ReadFeatureMemberAsync(reader, geometryFactory, collectionSrs, cancellationToken))
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
        GmlSrs inheritedSrs,
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

            var feature = await ParseFeatureElementAsync(reader, factory, inheritedSrs, cancellationToken);
            if (feature != null)
            {
                yield return feature;
            }
        }
    }

    private static async Task<IFeature?> ParseFeatureElementAsync(
        XmlReader reader,
        GeometryFactory factory,
        GmlSrs inheritedSrs,
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
                var geom = await TryParseGeometryAsync(reader, factory, inheritedSrs, cancellationToken);
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
            StringBuilder? propertyTextBuilder = null;

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
                    (propertyTextBuilder ??= new StringBuilder()).Append(reader.Value);
                    continue;
                }

                if (reader.NodeType == XmlNodeType.Element && IsGmlNamespace(reader.NamespaceURI))
                {
                    var geom = await TryParseGeometryAsync(reader, factory, inheritedSrs, cancellationToken);
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
            else if (propertyTextBuilder != null)
            {
                AddAttribute(attributes, propertyLocalName, propertyTextBuilder.ToString());
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
        GmlSrs inheritedSrs,
        CancellationToken cancellationToken)
    {
        return reader.LocalName switch
        {
            "Point" => await ParsePointAsync(reader, factory, inheritedSrs, cancellationToken),
            "LineString" or "Curve" => await ParseLineStringAsync(reader, factory, inheritedSrs, cancellationToken),
            "LinearRing" => await ParseLinearRingAsync(reader, factory, inheritedSrs, cancellationToken),
            "Polygon" or "Surface" => await ParsePolygonAsync(reader, factory, inheritedSrs, cancellationToken),
            "MultiPoint" => await ParseMultiPointAsync(reader, factory, inheritedSrs, cancellationToken),
            "MultiLineString" or "MultiCurve" => await ParseMultiLineStringAsync(reader, factory, inheritedSrs, cancellationToken),
            "MultiPolygon" or "MultiSurface" => await ParseMultiPolygonAsync(reader, factory, inheritedSrs, cancellationToken),
            "MultiGeometry" or "GeometryCollection" => await ParseGeometryCollectionAsync(reader, factory, inheritedSrs, cancellationToken),
            _ => null
        };
    }

    // -----------------------------------------------------------------------
    // Geometry parsers
    // -----------------------------------------------------------------------

    private static async Task<Point?> ParsePointAsync(
        XmlReader reader,
        GeometryFactory factory,
        GmlSrs inheritedSrs,
        CancellationToken cancellationToken)
    {
        var elementLocalName = reader.LocalName;
        var depth = reader.Depth;
        var srs = ResolveElementSrs(reader.GetAttribute("srsName"), inheritedSrs);
        int? srid = srs.Srid;

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
                    if (srs.SwapAxes)
                    {
                        SwapXy(coordinate);
                    }

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
        GmlSrs inheritedSrs,
        CancellationToken cancellationToken)
    {
        var elementLocalName = reader.LocalName;
        var depth = reader.Depth;
        var srs = ResolveElementSrs(reader.GetAttribute("srsName"), inheritedSrs);
        int? srid = srs.Srid;

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
                    if (srs.SwapAxes)
                    {
                        SwapXy(coordinates);
                    }

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
        GmlSrs inheritedSrs,
        CancellationToken cancellationToken)
    {
        var elementLocalName = reader.LocalName;
        var depth = reader.Depth;
        var srs = ResolveElementSrs(reader.GetAttribute("srsName"), inheritedSrs);
        int? srid = srs.Srid;

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
                    if (srs.SwapAxes)
                    {
                        SwapXy(coordinates);
                    }

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
        GmlSrs inheritedSrs,
        CancellationToken cancellationToken)
    {
        var elementLocalName = reader.LocalName;
        var depth = reader.Depth;
        var srs = ResolveElementSrs(reader.GetAttribute("srsName"), inheritedSrs);
        int? srid = srs.Srid;
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
                var ring = await ParseRingMemberAsync(reader, factory, srs, cancellationToken);
                if (ring != null)
                {
                    outerRing = ring;
                }
            }
            else if (reader.LocalName is "innerBoundaryIs" or "interior")
            {
                var ring = await ParseRingMemberAsync(reader, factory, srs, cancellationToken);
                if (ring != null)
                {
                    innerRings.Add(ring);
                }
            }
            else if (reader.LocalName == "LinearRing")
            {
                // GML 2 may nest LinearRing directly under Polygon
                var ring = await ParseLinearRingAsync(reader, factory, srs, cancellationToken);
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
        GmlSrs inheritedSrs,
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
                return await ParseLinearRingAsync(reader, factory, inheritedSrs, cancellationToken);
            }
        }

        return null;
    }

    private static async Task<MultiPoint?> ParseMultiPointAsync(
        XmlReader reader,
        GeometryFactory factory,
        GmlSrs inheritedSrs,
        CancellationToken cancellationToken)
    {
        var elementLocalName = reader.LocalName;
        var depth = reader.Depth;
        var srs = ResolveElementSrs(reader.GetAttribute("srsName"), inheritedSrs);
        int? srid = srs.Srid;
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
                var point = await ParsePointAsync(reader, factory, srs, cancellationToken);
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
        GmlSrs inheritedSrs,
        CancellationToken cancellationToken)
    {
        var elementLocalName = reader.LocalName;
        var depth = reader.Depth;
        var srs = ResolveElementSrs(reader.GetAttribute("srsName"), inheritedSrs);
        int? srid = srs.Srid;
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

                var line = await ParseLineStringAsync(reader, factory, srs, cancellationToken);
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
        GmlSrs inheritedSrs,
        CancellationToken cancellationToken)
    {
        var elementLocalName = reader.LocalName;
        var depth = reader.Depth;
        var srs = ResolveElementSrs(reader.GetAttribute("srsName"), inheritedSrs);
        int? srid = srs.Srid;
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

                var polygon = await ParsePolygonAsync(reader, factory, srs, cancellationToken);
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
        GmlSrs inheritedSrs,
        CancellationToken cancellationToken)
    {
        var elementLocalName = reader.LocalName;
        var depth = reader.Depth;
        var srs = ResolveElementSrs(reader.GetAttribute("srsName"), inheritedSrs);
        int? srid = srs.Srid;
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

            var geom = await TryParseGeometryAsync(reader, factory, srs, cancellationToken);
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
        var tuples = text.Split(_coordinateSeparators, StringSplitOptions.RemoveEmptyEntries);

        return tuples
            .Select(ParseGml2CoordinateTuple)
            .Where(coordinate => coordinate != null)
            .Select(coordinate => coordinate!)
            .ToArray();
    }

    private static Coordinate? ParseGml2CoordinateTuple(string tuple)
    {
        var parts = tuple.Split(',');
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

    internal static int? ParseSrsNameToSrid(string? srsName)
    {
        if (string.IsNullOrWhiteSpace(srsName))
        {
            return null;
        }

        var trimmed = srsName.Trim();

        // OGC CRS84 (WGS 84 in long/lat order): urn:ogc:def:crs:OGC:1.3:CRS84, CRS:84,
        // http://www.opengis.net/def/crs/OGC/1.3/CRS84. Treated as EPSG:4326 — the ordinates are
        // already long/lat (the axis order this reader reads as-written), so no swap is implied.
        // This check must precede the generic trailing-integer parse below, otherwise "CRS:84"
        // would be mis-read as SRID 84.
        if (trimmed.EndsWith("CRS84", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith("CRS:84", StringComparison.OrdinalIgnoreCase))
        {
            return 4326;
        }

        // "EPSG:4326"
        var colonIdx = trimmed.LastIndexOf(':');
        if (colonIdx >= 0 && int.TryParse(trimmed.AsSpan(colonIdx + 1), out var srid) && srid > 0)
        {
            return srid;
        }

        // "http://www.opengis.net/gml/srs/epsg.xml#4326"
        var hashIdx = trimmed.LastIndexOf('#');
        if (hashIdx >= 0 && int.TryParse(trimmed.AsSpan(hashIdx + 1), out var sridHash) && sridHash > 0)
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

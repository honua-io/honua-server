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

    private const int MaxGeometryCollectionDepth = 32;
    private const string FormatLabel = "GML";

    private static readonly char[] _coordinateSeparators = { ' ', '\n', '\r', '\t' };

    private readonly record struct GmlGeometryContext(XmlReader Reader, GeometryFactory Factory, GmlSrs InheritedSrs);

    private readonly record struct GmlCoordinateQuery(GmlSrs InheritedSrs, string PrimaryTag);

    private readonly record struct GmlCoordinatePayload(string Text, int? Dimension, GmlSrs ElementSrs);

    private static void EnsureXmlDepth(XmlReader reader)
        => BoundedXmlCoordinateTextReader.EnsureXmlDepth(reader, FormatLabel);

    private readonly record struct GmlSrs(int? Srid, bool SwapAxes, int? SrsDimension = null)
    {
        public static readonly GmlSrs None = new(null, false, null);
    }

    private static GmlSrs ResolveElementSrs(XmlReader reader, GmlSrs inherited)
        => ResolveElementSrs(
            reader.GetAttribute("srsName"),
            reader.GetAttribute("srsDimension") ?? reader.GetAttribute("dimension"),
            inherited);

    private static GmlSrs ResolveElementSrs(string? srsName, GmlSrs inherited)
        => ResolveElementSrs(srsName, null, inherited);

    private static GmlSrs ResolveElementSrs(string? srsName, string? srsDimensionText, GmlSrs inherited)
    {
        var dimension = ParseSrsDimension(srsDimensionText) ?? inherited.SrsDimension;
        if (string.IsNullOrWhiteSpace(srsName))
        {
            return inherited with { SrsDimension = dimension };
        }

        var srid = ParseSrsNameToSrid(srsName);
        if (srid is null)
        {
            return inherited with { SrsDimension = dimension };
        }

        return new GmlSrs(srid, RequiresAxisSwap(srsName, srid.Value), dimension);
    }

    private static int? ParseSrsDimension(string? srsDimensionText)
    {
        if (!string.IsNullOrWhiteSpace(srsDimensionText) &&
            int.TryParse(srsDimensionText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dim) &&
            dim is 2 or 3)
        {
            return dim;
        }

        return null;
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
            EnsureXmlDepth(reader);

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
            EnsureXmlDepth(reader);

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            var localName = reader.LocalName;

            // Capture the srsName from the root collection element
            if (!collectionSrsResolved && (localName is "FeatureCollection" or "SimpleFeatureCollection"))
            {
                var srsName = reader.GetAttribute("srsName");
                var srsDimension = reader.GetAttribute("srsDimension") ?? reader.GetAttribute("dimension");
                if (!string.IsNullOrWhiteSpace(srsName) || !string.IsNullOrWhiteSpace(srsDimension))
                {
                    collectionSrs = ResolveElementSrs(srsName, srsDimension, collectionSrs);
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
            EnsureXmlDepth(reader);

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
            EnsureXmlDepth(reader);

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
                var geom = await TryParseGeometryAsync(new GmlGeometryContext(reader, factory, inheritedSrs), 0, cancellationToken);
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
            if (reader.IsEmptyElement)
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
                EnsureXmlDepth(reader);

                if (reader.NodeType == XmlNodeType.EndElement &&
                    reader.LocalName == propertyLocalName &&
                    reader.Depth == propertyDepth)
                {
                    break;
                }

                if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
                {
                    BoundedXmlCoordinateTextReader.AppendBoundedText(propertyTextBuilder ??= new StringBuilder(), reader.Value, FormatLabel);
                    continue;
                }

                if (reader.NodeType == XmlNodeType.Element && IsGmlNamespace(reader.NamespaceURI))
                {
                    var geom = await TryParseGeometryAsync(new GmlGeometryContext(reader, factory, inheritedSrs), 0, cancellationToken);
                    if (geom != null)
                    {
                        propertyGeom = geom;
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
        GmlGeometryContext context,
        int collectionDepth,
        CancellationToken cancellationToken)
    {
        return context.Reader.LocalName switch
        {
            "Point" => await ParsePointAsync(context.Reader, context.Factory, context.InheritedSrs, cancellationToken),
            "LineString" or "Curve" => await ParseLineStringAsync(context, cancellationToken),
            "LinearRing" => await ParseLinearRingAsync(context.Reader, context.Factory, context.InheritedSrs, cancellationToken),
            "Polygon" or "Surface" => await ParsePolygonAsync(context.Reader, context.Factory, context.InheritedSrs, cancellationToken),
            "MultiPoint" => await ParseMultiPointAsync(context.Reader, context.Factory, context.InheritedSrs, cancellationToken),
            "MultiLineString" or "MultiCurve" => await ParseMultiLineStringAsync(context.Reader, context.Factory, context.InheritedSrs, cancellationToken),
            "MultiPolygon" or "MultiSurface" => await ParseMultiPolygonAsync(context.Reader, context.Factory, context.InheritedSrs, cancellationToken),
            "MultiGeometry" or "GeometryCollection" => await ParseGeometryCollectionAsync(context, collectionDepth, cancellationToken),
            _ => null
        };
    }

    // -----------------------------------------------------------------------
    // Geometry parsers
    // -----------------------------------------------------------------------

    private static async Task<GmlCoordinatePayload?> ReadGeometryCoordinatesAsync(
        XmlReader reader,
        GmlCoordinateQuery query,
        CancellationToken cancellationToken)
    {
        var elementLocalName = reader.LocalName;
        var depth = reader.Depth;
        var srs = ResolveElementSrs(reader, query.InheritedSrs);

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureXmlDepth(reader);

            if (reader.NodeType == XmlNodeType.EndElement &&
                reader.LocalName == elementLocalName &&
                reader.Depth == depth)
            {
                break;
            }

            if (reader.NodeType == XmlNodeType.Element &&
                (reader.LocalName == query.PrimaryTag || reader.LocalName == "coordinates"))
            {
                var posDimension = ParseSrsDimension(reader.GetAttribute("srsDimension") ?? reader.GetAttribute("dimension")) ?? srs.SrsDimension;
                var coords = await BoundedXmlCoordinateTextReader.ReadElementTextAsync(reader, FormatLabel, cancellationToken);
                return new GmlCoordinatePayload(coords, posDimension, srs);
            }
        }

        return null;
    }

    private static async Task<Point?> ParsePointAsync(
        XmlReader reader,
        GeometryFactory factory,
        GmlSrs inheritedSrs,
        CancellationToken cancellationToken)
    {
        var payload = await ReadGeometryCoordinatesAsync(reader, new GmlCoordinateQuery(inheritedSrs, "pos"), cancellationToken);
        if (payload is null || ParsePos(payload.Value.Text, payload.Value.Dimension) is not { } coordinate)
        {
            return null;
        }

        if (payload.Value.ElementSrs.SwapAxes)
        {
            SwapXy(coordinate);
        }

        var point = factory.CreatePoint(coordinate);
        if (payload.Value.ElementSrs.Srid is { } srid)
        {
            point.SRID = srid;
        }

        return point;
    }

    private static async Task<LineString?> ParseLineStringAsync(
        GmlGeometryContext context,
        CancellationToken cancellationToken)
    {
        var payload = await ReadGeometryCoordinatesAsync(context.Reader, new GmlCoordinateQuery(context.InheritedSrs, "posList"), cancellationToken);
        if (payload is null)
        {
            return null;
        }

        var coordinates = ParsePosList(payload.Value.Text, payload.Value.Dimension);
        if (coordinates.Length < 2)
        {
            return null;
        }

        if (payload.Value.ElementSrs.SwapAxes)
        {
            SwapXy(coordinates);
        }

        var line = context.Factory.CreateLineString(coordinates);
        if (payload.Value.ElementSrs.Srid is { } srid)
        {
            line.SRID = srid;
        }

        return line;
    }

    private static async Task<LinearRing?> ParseLinearRingAsync(
        XmlReader reader,
        GeometryFactory factory,
        GmlSrs inheritedSrs,
        CancellationToken cancellationToken)
    {
        var payload = await ReadGeometryCoordinatesAsync(reader, new GmlCoordinateQuery(inheritedSrs, "posList"), cancellationToken);
        if (payload is null)
        {
            return null;
        }

        var coordinates = ParsePosList(payload.Value.Text, payload.Value.Dimension);
        if (coordinates.Length < 3)
        {
            return null;
        }

        if (payload.Value.ElementSrs.SwapAxes)
        {
            SwapXy(coordinates);
        }

        coordinates = EnsureClosedRing(coordinates);
        if (coordinates.Length < 4)
        {
            return null;
        }

        var ring = factory.CreateLinearRing(coordinates);
        if (payload.Value.ElementSrs.Srid is { } srid)
        {
            ring.SRID = srid;
        }

        return ring;
    }

    private static async Task<Polygon?> ParsePolygonAsync(
        XmlReader reader,
        GeometryFactory factory,
        GmlSrs inheritedSrs,
        CancellationToken cancellationToken)
    {
        var elementLocalName = reader.LocalName;
        var depth = reader.Depth;
        var srs = ResolveElementSrs(reader, inheritedSrs);
        int? srid = srs.Srid;
        LinearRing? outerRing = null;
        var innerRings = new List<LinearRing>();

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureXmlDepth(reader);

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
                if (innerRings.Count >= BoundedXmlCoordinateTextReader.MaxPolygonRings)
                {
                    throw new InvalidDataException(
                        $"GML polygon interior ring count exceeds the maximum of {BoundedXmlCoordinateTextReader.MaxPolygonRings:N0}.");
                }

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
        var srs = ResolveElementSrs(reader, inheritedSrs);

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
                return await ParseLinearRingAsync(reader, factory, srs, cancellationToken);
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
        var srs = ResolveElementSrs(reader, inheritedSrs);
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
        var srs = ResolveElementSrs(reader, inheritedSrs);
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

                var line = await ParseLineStringAsync(new GmlGeometryContext(reader, factory, srs), cancellationToken);
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
        var srs = ResolveElementSrs(reader, inheritedSrs);
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
        GmlGeometryContext context,
        int collectionDepth,
        CancellationToken cancellationToken)
    {
        if (collectionDepth > MaxGeometryCollectionDepth)
        {
            throw new InvalidDataException(
                $"GML geometry collection nesting depth exceeds the maximum of {MaxGeometryCollectionDepth}.");
        }

        var reader = context.Reader;
        var elementLocalName = reader.LocalName;
        var depth = reader.Depth;
        var srs = ResolveElementSrs(reader, context.InheritedSrs);
        var memberContext = new GmlGeometryContext(reader, context.Factory, srs);
        var geometries = new List<NtsGeometry>();

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureXmlDepth(reader);

            if (reader.NodeType == XmlNodeType.EndElement &&
                reader.LocalName == elementLocalName &&
                reader.Depth == depth)
            {
                break;
            }

            var geom = await TryParseCollectionMemberAsync(memberContext, collectionDepth + 1, cancellationToken);
            if (geom != null)
            {
                geometries.Add(geom);
            }
        }

        return BuildGeometryCollection(context.Factory, geometries, srs.Srid);
    }

    private static Task<NtsGeometry?> TryParseCollectionMemberAsync(
        GmlGeometryContext context,
        int nextDepth,
        CancellationToken cancellationToken)
    {
        var reader = context.Reader;
        if (reader.NodeType != XmlNodeType.Element || !IsCollectionGeometryMember(reader))
        {
            return Task.FromResult<NtsGeometry?>(null);
        }

        return TryParseGeometryAsync(context, nextDepth, cancellationToken);
    }

    private static bool IsCollectionGeometryMember(XmlReader reader)
        => IsGmlNamespace(reader.NamespaceURI) ||
           reader.LocalName is "Point" or "LineString" or "Curve" or "Polygon" or "Surface"
               or "MultiPoint" or "MultiLineString" or "MultiCurve" or "MultiPolygon" or "MultiSurface"
               or "LinearRing";

    private static GeometryCollection? BuildGeometryCollection(
        GeometryFactory factory,
        List<NtsGeometry> geometries,
        int? srid)
    {
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

    private static Coordinate? ParsePos(string posText, int? srsDimension = null)
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

        if (srsDimension != 2 &&
            parts.Length >= 3 &&
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        {
            return new CoordinateZ(x, y, z);
        }

        return new Coordinate(x, y);
    }

    private static Coordinate[] ParsePosList(string text, int? srsDimension = null)
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
        return ParseGml3PosList(trimmed, srsDimension);
    }

    private static Coordinate[] EnsureClosedRing(Coordinate[] coordinates)
    {
        if (coordinates.Length >= 3 && !coordinates[0].Equals2D(coordinates[^1]))
        {
            var closed = new Coordinate[coordinates.Length + 1];
            Array.Copy(coordinates, closed, coordinates.Length);
            closed[^1] = coordinates[0].Copy();
            return closed;
        }

        return coordinates;
    }

    private static Coordinate[] ParseGml2Coordinates(string text)
    {
        BoundedXmlCoordinateTextReader.EnsureCoordinateLength(text, FormatLabel);
        var tuples = text.Split(_coordinateSeparators, StringSplitOptions.RemoveEmptyEntries);
        BoundedXmlCoordinateTextReader.EnsureVertexCount(tuples.Length, FormatLabel);

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

    private static Coordinate[] ParseGml3PosList(string text, int? srsDimension = null)
    {
        BoundedXmlCoordinateTextReader.EnsureCoordinateLength(text, FormatLabel);
        var parts = text.Split(_coordinateSeparators, StringSplitOptions.RemoveEmptyEntries);
        var is3D = srsDimension switch
        {
            3 => parts.Length >= 3,
            2 => false,
            _ => parts.Length >= 3 && parts.Length % 3 == 0 && parts.Length % 2 != 0
        };

        var stride = is3D ? 3 : 2;
        BoundedXmlCoordinateTextReader.EnsureVertexCount(parts.Length / stride, FormatLabel);
        var coords = new List<Coordinate>(parts.Length / stride);
        for (var i = 0; i + stride - 1 < parts.Length; i += stride)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !double.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                continue;
            }

            if (is3D)
            {
                if (double.TryParse(parts[i + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                {
                    coords.Add(new CoordinateZ(x, y, z));
                }
            }
            else
            {
                coords.Add(new Coordinate(x, y));
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
        => BoundedXmlCoordinateTextReader.CreateReaderSettings();
}

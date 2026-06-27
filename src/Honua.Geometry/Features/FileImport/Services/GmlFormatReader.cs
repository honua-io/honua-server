// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Core.Features.FileImport.Services;

/// <summary>
/// Streaming GML (Geography Markup Language) format reader. Parses feature members
/// from a GML document into features using <see cref="XmlReader"/> for memory-efficient
/// processing, mirroring the streaming approach of <c>KmlFormatReader</c>.
/// </summary>
/// <remarks>
/// Supports the common GML 2 and GML 3.x geometry encodings produced by WFS servers and
/// GIS exports: <c>Point</c>, <c>LineString</c>, <c>LinearRing</c>, <c>Polygon</c>, and the
/// multi-geometry collections (<c>MultiPoint</c>, <c>MultiLineString</c>/<c>MultiCurve</c>,
/// <c>MultiPolygon</c>/<c>MultiSurface</c>, <c>MultiGeometry</c>). Coordinates are read from
/// <c>gml:coordinates</c> (GML 2), <c>gml:pos</c>/<c>gml:posList</c> (GML 3), and
/// <c>gml:coord</c>. Non-geometry leaf elements within a feature become attributes keyed by
/// their local name. Coordinates are read in document order (first ordinate = X/longitude);
/// CRS-specific axis swapping is not applied.
/// </remarks>
internal static class GmlFormatReader
{
    private static readonly char[] _whitespaceSeparators = { ' ', '\n', '\r', '\t' };

    /// <summary>
    /// Streams features from a GML document using <see cref="XmlReader"/> for memory efficiency.
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

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            var localName = reader.LocalName;

            // Single-member wrappers (gml:featureMember, wfs:member) hold exactly one feature.
            if (localName is "featureMember" or "member")
            {
                IFeature? feature = await TryParseFeatureAsync(reader, geometryFactory, cancellationToken);
                if (feature != null)
                {
                    yield return feature;
                }
            }
            // Plural containers (gml:featureMembers, wfs:members) hold several feature elements.
            else if (localName is "featureMembers" or "members")
            {
                await foreach (var feature in ParseFeatureMembersAsync(reader, geometryFactory, cancellationToken))
                {
                    yield return feature;
                }
            }
        }
    }

    private static async IAsyncEnumerable<IFeature> ParseFeatureMembersAsync(
        XmlReader reader,
        GeometryFactory factory,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (reader.IsEmptyElement)
        {
            yield break;
        }

        var containerDepth = reader.Depth;

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == containerDepth)
            {
                break;
            }

            if (reader.NodeType == XmlNodeType.Element)
            {
                IFeature? feature = await TryParseFeatureAsync(reader, factory, cancellationToken);
                if (feature != null)
                {
                    yield return feature;
                }
            }
        }
    }

    private static async Task<IFeature?> TryParseFeatureAsync(
        XmlReader reader,
        GeometryFactory factory,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ParseFeatureAsync(reader, factory, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Skip a single malformed feature rather than aborting the whole import
            // stream, consistent with KmlFormatReader/WktFormatReader/CsvFormatReader.
            return null;
        }
    }

    private static async Task<IFeature?> ParseFeatureAsync(
        XmlReader reader,
        GeometryFactory factory,
        CancellationToken cancellationToken)
    {
        var attributes = new AttributesTable();
        var holder = new GeometryHolder();

        // Walk the entire member subtree, collecting the first geometry encountered, the
        // feature's gml:id, and any simple-text leaf elements as attributes.
        await ProcessElementAsync(reader, factory, attributes, holder, cancellationToken);

        if (holder.Geometry == null && attributes.Count == 0)
        {
            return null;
        }

        return new Feature(holder.Geometry, attributes);
    }

    /// <summary>
    /// Recursively consumes the element the reader is positioned on (through its matching
    /// end element). Geometry elements are parsed into <paramref name="holder"/>; simple-text
    /// leaf elements are added to <paramref name="attributes"/> keyed by local name.
    /// </summary>
    private static async Task ProcessElementAsync(
        XmlReader reader,
        GeometryFactory factory,
        AttributesTable attributes,
        GeometryHolder holder,
        CancellationToken cancellationToken)
    {
        var localName = reader.LocalName;

        if (IsGeometryElement(localName))
        {
            var geometry = await ParseGeometryAsync(reader, factory, cancellationToken);
            if (geometry != null && holder.Geometry == null)
            {
                holder.Geometry = geometry;
            }

            return;
        }

        // Capture the first gml:id / fid encountered on a non-geometry element so feature
        // identity survives the round-trip. The wrapping *Member element rarely carries an id,
        // so the first match is the feature element itself rather than a geometry primitive.
        if (!attributes.Exists("gml_id"))
        {
            var gmlId = reader.GetAttribute("id", "http://www.opengis.net/gml/3.2")
                ?? reader.GetAttribute("id", "http://www.opengis.net/gml")
                ?? reader.GetAttribute("fid");
            if (!string.IsNullOrWhiteSpace(gmlId))
            {
                AddOrReplaceAttribute(attributes, "gml_id", gmlId);
            }
        }

        if (reader.IsEmptyElement)
        {
            return;
        }

        var depth = reader.Depth;
        string? text = null;
        var hasChildElements = false;

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                break;
            }

            if (reader.NodeType == XmlNodeType.Element)
            {
                hasChildElements = true;
                await ProcessElementAsync(reader, factory, attributes, holder, cancellationToken);
            }
            else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
            {
                text ??= reader.Value;
            }
        }

        if (!hasChildElements && !string.IsNullOrWhiteSpace(text))
        {
            AddOrReplaceAttribute(attributes, localName, text);
        }
    }

    private static bool IsGeometryElement(string localName) => localName switch
    {
        "Point" or "LineString" or "LinearRing" or "Polygon" or "Curve" or "Surface"
            or "MultiPoint" or "MultiLineString" or "MultiCurve"
            or "MultiPolygon" or "MultiSurface" or "MultiGeometry" => true,
        _ => false
    };

    private static async Task<NtsGeometry?> ParseGeometryAsync(
        XmlReader reader,
        GeometryFactory factory,
        CancellationToken cancellationToken)
    {
        switch (reader.LocalName)
        {
            case "Point":
                {
                    var coords = await ReadPrimitiveCoordinatesAsync(reader, cancellationToken);
                    return coords.Length > 0 ? factory.CreatePoint(coords[0]) : null;
                }
            case "LineString":
            case "Curve":
                {
                    var coords = await ReadPrimitiveCoordinatesAsync(reader, cancellationToken);
                    return coords.Length >= 2 ? factory.CreateLineString(coords) : null;
                }
            case "LinearRing":
                {
                    var coords = EnsureClosedRing(await ReadPrimitiveCoordinatesAsync(reader, cancellationToken));
                    return coords.Length >= 4 ? factory.CreateLinearRing(coords) : null;
                }
            case "Polygon":
            case "Surface":
                return await ParsePolygonAsync(reader, factory, cancellationToken);
            case "MultiPoint":
            case "MultiLineString":
            case "MultiCurve":
            case "MultiPolygon":
            case "MultiSurface":
            case "MultiGeometry":
                return await ParseGeometryCollectionAsync(reader, factory, cancellationToken);
            default:
                return null;
        }
    }

    private static async Task<NtsGeometry?> ParseGeometryCollectionAsync(
        XmlReader reader,
        GeometryFactory factory,
        CancellationToken cancellationToken)
    {
        var geometries = new List<NtsGeometry>();
        var collectionName = reader.LocalName;

        if (reader.IsEmptyElement)
        {
            return null;
        }

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == collectionName)
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            // Members may be the geometry directly (inside *Member / *Members wrappers,
            // which are transparent), so descend until a geometry primitive is reached.
            if (IsGeometryElement(reader.LocalName))
            {
                var geometry = await ParseGeometryAsync(reader, factory, cancellationToken);
                if (geometry != null)
                {
                    geometries.Add(geometry);
                }
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
        var polygonName = reader.LocalName;

        if (reader.IsEmptyElement)
        {
            return null;
        }

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == polygonName)
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                // GML 3: <gml:exterior>/<gml:interior>. GML 2: <gml:outerBoundaryIs>/<gml:innerBoundaryIs>.
                case "exterior":
                case "outerBoundaryIs":
                    outerRing = await ParseBoundaryRingAsync(reader, factory, reader.LocalName, cancellationToken);
                    break;
                case "interior":
                case "innerBoundaryIs":
                    var ring = await ParseBoundaryRingAsync(reader, factory, reader.LocalName, cancellationToken);
                    if (ring != null)
                    {
                        innerRings.Add(ring);
                    }

                    break;
            }
        }

        return outerRing != null ? factory.CreatePolygon(outerRing, innerRings.ToArray()) : null;
    }

    private static async Task<LinearRing?> ParseBoundaryRingAsync(
        XmlReader reader,
        GeometryFactory factory,
        string boundaryName,
        CancellationToken cancellationToken)
    {
        if (reader.IsEmptyElement)
        {
            return null;
        }

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == boundaryName)
            {
                break;
            }

            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "LinearRing")
            {
                var coords = EnsureClosedRing(await ReadPrimitiveCoordinatesAsync(reader, cancellationToken));
                if (coords.Length >= 4)
                {
                    return factory.CreateLinearRing(coords);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Reads every coordinate contained in a geometry primitive element
    /// (<c>Point</c>/<c>LineString</c>/<c>LinearRing</c>), accumulating ordinates from
    /// <c>gml:pos</c>, <c>gml:posList</c>, <c>gml:coordinates</c>, and <c>gml:coord</c>.
    /// </summary>
    private static async Task<Coordinate[]> ReadPrimitiveCoordinatesAsync(
        XmlReader reader,
        CancellationToken cancellationToken)
    {
        var coordinates = new List<Coordinate>();
        var primitiveName = reader.LocalName;

        if (reader.IsEmptyElement)
        {
            return [];
        }

        // ReadElementContentAsStringAsync advances the reader PAST the element's end tag, so
        // after consuming a coordinate element we must re-evaluate the current node instead of
        // reading the next one (which would skip — and over-consume past — the primitive's end).
        var advance = true;
        while (true)
        {
            if (advance && !await reader.ReadAsync())
            {
                break;
            }

            advance = true;
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == primitiveName)
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                case "pos":
                    {
                        var value = await reader.ReadElementContentAsStringAsync();
                        var coord = ParsePosTuple(value);
                        if (coord != null)
                        {
                            coordinates.Add(coord);
                        }

                        advance = false;
                        break;
                    }
                case "posList":
                    {
                        var dimension = ParseDimension(reader.GetAttribute("srsDimension"));
                        var value = await reader.ReadElementContentAsStringAsync();
                        coordinates.AddRange(ParsePosList(value, dimension));
                        advance = false;
                        break;
                    }
                case "coordinates":
                    {
                        var value = await reader.ReadElementContentAsStringAsync();
                        coordinates.AddRange(ParseGmlCoordinates(value));
                        advance = false;
                        break;
                    }
                case "coord":
                    {
                        // ParseCoordElementAsync stops on the <coord> end tag, so a normal read advances.
                        var coord = await ParseCoordElementAsync(reader, cancellationToken);
                        if (coord != null)
                        {
                            coordinates.Add(coord);
                        }

                        break;
                    }
            }
        }

        return coordinates.ToArray();
    }

    private static async Task<Coordinate?> ParseCoordElementAsync(
        XmlReader reader,
        CancellationToken cancellationToken)
    {
        double? x = null;
        double? y = null;
        double? z = null;

        if (reader.IsEmptyElement)
        {
            return null;
        }

        // ReadElementContentAsStringAsync advances past each ordinate's end tag, so re-evaluate
        // the current node rather than reading the next one (see ReadPrimitiveCoordinatesAsync).
        var advance = true;
        while (true)
        {
            if (advance && !await reader.ReadAsync())
            {
                break;
            }

            advance = true;
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "coord")
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                case "X":
                    x = ParseOrdinate(await reader.ReadElementContentAsStringAsync());
                    advance = false;
                    break;
                case "Y":
                    y = ParseOrdinate(await reader.ReadElementContentAsStringAsync());
                    advance = false;
                    break;
                case "Z":
                    z = ParseOrdinate(await reader.ReadElementContentAsStringAsync());
                    advance = false;
                    break;
            }
        }

        if (x == null || y == null)
        {
            return null;
        }

        return z.HasValue ? new CoordinateZ(x.Value, y.Value, z.Value) : new Coordinate(x.Value, y.Value);
    }

    private static Coordinate? ParsePosTuple(string value)
    {
        var parts = value.Split(_whitespaceSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        if (!TryParseOrdinate(parts[0], out var x) || !TryParseOrdinate(parts[1], out var y))
        {
            return null;
        }

        if (parts.Length >= 3 && TryParseOrdinate(parts[2], out var z))
        {
            return new CoordinateZ(x, y, z);
        }

        return new Coordinate(x, y);
    }

    private static List<Coordinate> ParsePosList(string value, int dimension)
    {
        var parts = value.Split(_whitespaceSeparators, StringSplitOptions.RemoveEmptyEntries);
        var step = dimension < 2 ? 2 : dimension;
        var result = new List<Coordinate>(parts.Length / step);

        for (var i = 0; i + 1 < parts.Length; i += step)
        {
            if (!TryParseOrdinate(parts[i], out var x) || !TryParseOrdinate(parts[i + 1], out var y))
            {
                continue;
            }

            if (step >= 3 && i + 2 < parts.Length && TryParseOrdinate(parts[i + 2], out var z))
            {
                result.Add(new CoordinateZ(x, y, z));
            }
            else
            {
                result.Add(new Coordinate(x, y));
            }
        }

        return result;
    }

    private static List<Coordinate> ParseGmlCoordinates(string value)
    {
        // GML 2 <gml:coordinates> default delimiters: ',' between ordinates, whitespace
        // between tuples (cs/ts attributes overriding these are rarely emitted and not handled).
        var tuples = value.Split(_whitespaceSeparators, StringSplitOptions.RemoveEmptyEntries);
        var result = new List<Coordinate>(tuples.Length);

        foreach (var tuple in tuples)
        {
            var ordinates = tuple.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (ordinates.Length < 2 ||
                !TryParseOrdinate(ordinates[0], out var x) ||
                !TryParseOrdinate(ordinates[1], out var y))
            {
                continue;
            }

            if (ordinates.Length >= 3 && TryParseOrdinate(ordinates[2], out var z))
            {
                result.Add(new CoordinateZ(x, y, z));
            }
            else
            {
                result.Add(new Coordinate(x, y));
            }
        }

        return result;
    }

    private static int ParseDimension(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dimension) &&
            dimension >= 2)
        {
            return dimension;
        }

        return 2;
    }

    private static double? ParseOrdinate(string value) =>
        TryParseOrdinate(value, out var parsed) ? parsed : null;

    private static bool TryParseOrdinate(string value, out double parsed) =>
        double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);

    /// <summary>
    /// Ensures a ring's coordinates form a closed loop. NTS <c>CreateLinearRing</c> throws if
    /// the first and last coordinates differ, which would otherwise abort the entire import
    /// stream for a single unclosed ring. Mirrors the closure logic in <c>KmlFormatReader</c>.
    /// </summary>
    private static Coordinate[] EnsureClosedRing(Coordinate[] coordinates)
    {
        if (coordinates.Length < 2 || coordinates[0].Equals2D(coordinates[^1]))
        {
            return coordinates;
        }

        var closed = new Coordinate[coordinates.Length + 1];
        Array.Copy(coordinates, closed, coordinates.Length);
        closed[coordinates.Length] = coordinates[0].Copy();
        return closed;
    }

    private static void AddOrReplaceAttribute(AttributesTable attributes, string name, object? value)
    {
        if (attributes.Exists(name))
        {
            attributes.DeleteAttribute(name);
        }

        attributes.Add(name, value);
    }

    private sealed class GeometryHolder
    {
        public NtsGeometry? Geometry { get; set; }
    }
}

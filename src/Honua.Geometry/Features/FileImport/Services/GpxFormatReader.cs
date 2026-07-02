// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.FileImport.Services;

/// <summary>
/// Streaming GPX format reader. Parses GPX waypoints, tracks, and routes into features
/// using XmlReader for memory-efficient processing.
/// </summary>
internal static class GpxFormatReader
{
    /// <summary>
    /// Streams features from a GPX file using XmlReader for memory efficiency.
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

            if (reader.NodeType == XmlNodeType.Element)
            {
                IFeature? feature = null;
                switch (reader.LocalName)
                {
                    case "wpt":
                        feature = await ParseWaypointAsync(reader, geometryFactory, cancellationToken);
                        break;
                    case "trk":
                        feature = await ParseTrackAsync(reader, geometryFactory, cancellationToken);
                        break;
                    case "rte":
                        feature = await ParseRouteAsync(reader, geometryFactory, cancellationToken);
                        break;
                }

                if (feature != null)
                    yield return feature;
            }
        }
    }

    private static async Task<IFeature?> ParseWaypointAsync(
        XmlReader reader,
        GeometryFactory factory,
        CancellationToken cancellationToken)
    {
        var lat = reader.GetAttribute("lat");
        var lon = reader.GetAttribute("lon");

        if (lat == null || lon == null ||
            !double.TryParse(lat, NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) ||
            !double.TryParse(lon, NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude))
            return null;

        var attributes = new AttributesTable();
        var geometry = factory.CreatePoint(new Coordinate(longitude, latitude));

        using (var subtree = reader.ReadSubtree())
        {
            await subtree.ReadAsync();
            await ReadLeafAttributesAsync(subtree, attributes, cancellationToken);
        }

        return new Feature(geometry, attributes);
    }

    /// <summary>
    /// Walks the already-opened element subtree and records leaf (simple-text) descendants as
    /// attributes keyed by their local name. Container elements such as GPX <c>&lt;extensions&gt;</c>
    /// (which GDAL uses to carry source fields, e.g. <c>&lt;ogr:zone_code&gt;030&lt;/ogr:zone_code&gt;</c>)
    /// are descended into rather than read as text. This avoids the
    /// <see cref="System.Xml.XmlException"/> that <c>ReadElementContentAsString</c> throws on an
    /// element that has child elements (honua-server#2354).
    /// </summary>
    private static async Task ReadLeafAttributesAsync(
        XmlReader reader,
        AttributesTable attributes,
        CancellationToken cancellationToken)
    {
        string? pendingName = null;

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    if (reader.IsEmptyElement)
                    {
                        // A self-closing leaf (e.g. <name/>). The <extensions> wrapper carries no
                        // value of its own, so it is never recorded as an attribute.
                        if (!IsContainerElement(reader.LocalName) && !attributes.Exists(reader.LocalName))
                        {
                            attributes.Add(reader.LocalName, string.Empty);
                        }

                        pendingName = null;
                    }
                    else
                    {
                        // Candidate leaf. If this turns out to be a container, the next Element
                        // read overwrites pendingName before any Text arrives, so container
                        // wrappers never produce a spurious attribute.
                        pendingName = reader.LocalName;
                    }

                    break;

                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                    if (pendingName != null && !attributes.Exists(pendingName))
                    {
                        attributes.Add(pendingName, reader.Value);
                    }

                    pendingName = null;
                    break;

                case XmlNodeType.EndElement:
                    pendingName = null;
                    break;
            }
        }
    }

    private static bool IsContainerElement(string localName) =>
        string.Equals(localName, "extensions", StringComparison.OrdinalIgnoreCase);

    private static async Task<IFeature?> ParseTrackAsync(
        XmlReader reader,
        GeometryFactory factory,
        CancellationToken cancellationToken)
    {
        var attributes = new AttributesTable();
        var allCoordinates = new List<Coordinate>();

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "trk")
                break;

            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.LocalName == "name")
                {
                    attributes.Add("name", await reader.ReadElementContentAsStringAsync());
                }
                else if (reader.LocalName == "trkpt")
                {
                    var lat = reader.GetAttribute("lat");
                    var lon = reader.GetAttribute("lon");
                    if (lat != null && lon != null &&
                        double.TryParse(lat, NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) &&
                        double.TryParse(lon, NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude))
                    {
                        allCoordinates.Add(new Coordinate(longitude, latitude));
                    }
                }
            }
        }

        if (allCoordinates.Count >= 2)
        {
            var geometry = factory.CreateLineString(allCoordinates.ToArray());
            return new Feature(geometry, attributes);
        }

        return null;
    }

    private static async Task<IFeature?> ParseRouteAsync(
        XmlReader reader,
        GeometryFactory factory,
        CancellationToken cancellationToken)
    {
        var attributes = new AttributesTable();
        var coordinates = new List<Coordinate>();

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "rte")
                break;

            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.LocalName == "name")
                {
                    attributes.Add("name", await reader.ReadElementContentAsStringAsync());
                }
                else if (reader.LocalName == "rtept")
                {
                    var lat = reader.GetAttribute("lat");
                    var lon = reader.GetAttribute("lon");
                    if (lat != null && lon != null &&
                        double.TryParse(lat, NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) &&
                        double.TryParse(lon, NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude))
                    {
                        coordinates.Add(new Coordinate(longitude, latitude));
                    }
                }
            }
        }

        if (coordinates.Count >= 2)
        {
            var geometry = factory.CreateLineString(coordinates.ToArray());
            return new Feature(geometry, attributes);
        }

        return null;
    }
}

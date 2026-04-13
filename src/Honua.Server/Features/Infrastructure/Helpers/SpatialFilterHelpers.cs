// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Infrastructure.Helpers;

/// <summary>
/// Shared helpers for creating spatial filters from bounding box extents.
/// Used by MapServer (export, tile, WMS) and OGC Tiles endpoints.
/// </summary>
internal static class SpatialFilterHelpers
{
    private static readonly GeometryFactory _wgs84Factory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    [ThreadStatic]
    private static WKBWriter? _wkbWriter;

    private static WKBWriter GetWkbWriter() => _wkbWriter ??= new WKBWriter();

    /// <summary>
    /// Creates a spatial intersect filter from a bounding box.
    /// </summary>
    public static SpatialFilter CreateBboxSpatialFilter(
        double minX, double minY, double maxX, double maxY, int srid)
    {
        var wkb = CreateEnvelopeWkb(minX, minY, maxX, maxY);
        return SpatialFilter.Create(wkb, SpatialRelationship.Intersects, srid);
    }

    /// <summary>
    /// Creates a WKB polygon representing a bounding box envelope.
    /// </summary>
    public static byte[] CreateEnvelopeWkb(double minX, double minY, double maxX, double maxY)
    {
        var geometry = CreateEnvelopeGeometry(minX, minY, maxX, maxY);
        return GetWkbWriter().Write(geometry);
    }

    private static Geometry CreateEnvelopeGeometry(double minX, double minY, double maxX, double maxY)
    {
        if (BoundingBox.Create(minX, minY, maxX, maxY).IsAntimeridianCrossing)
        {
            var eastHemisphere = CreateEnvelopePolygon(minX, minY, 180.0, maxY);
            var westHemisphere = CreateEnvelopePolygon(-180.0, minY, maxX, maxY);
            return _wgs84Factory.CreateMultiPolygon([eastHemisphere, westHemisphere]);
        }

        return _wgs84Factory.ToGeometry(new Envelope(minX, maxX, minY, maxY));
    }

    private static Polygon CreateEnvelopePolygon(double minX, double minY, double maxX, double maxY)
        => _wgs84Factory.CreatePolygon(
            [
                new Coordinate(minX, minY),
                new Coordinate(maxX, minY),
                new Coordinate(maxX, maxY),
                new Coordinate(minX, maxY),
                new Coordinate(minX, minY)
            ]);
}

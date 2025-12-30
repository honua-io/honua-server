// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Server.Features.OgcFeatures.Models;

/// <summary>
/// Extension methods for converting between OGC models and shared components
/// </summary>
public static class OgcExtensions
{
    /// <summary>
    /// Converts a FeatureExtent to OGC SpatialExtent
    /// </summary>
    /// <param name="extent">Feature extent</param>
    /// <returns>OGC spatial extent</returns>
    public static SpatialExtent ToSpatialExtent(this FeatureExtent extent)
        => new()
        {
            BoundingBox = [extent.ToBoundingBox().ToImmutableArray()],
            Crs = ExtentExtensions.ToOgcCrsUri(extent.SpatialReference)
        };

    /// <summary>
    /// Converts OGC SpatialExtent to FeatureExtent
    /// </summary>
    /// <param name="spatialExtent">OGC spatial extent</param>
    /// <returns>Feature extent</returns>
    public static FeatureExtent ToFeatureExtent(this SpatialExtent spatialExtent)
    {
        if (spatialExtent.BoundingBox.Length == 0 || spatialExtent.BoundingBox[0].Length != 4)
            throw new ArgumentException("SpatialExtent must have a valid bounding box");

        var bbox = spatialExtent.BoundingBox[0];
        var srid = ExtentExtensions.ExtractSridFromCrs(spatialExtent.Crs);

        return FeatureExtent.Create(bbox[0], bbox[1], bbox[2], bbox[3], srid);
    }

    /// <summary>
    /// Converts a shared GeoJsonFeatureBase to OGC GeoJsonFeature
    /// </summary>
    /// <param name="featureBase">Shared GeoJSON feature base</param>
    /// <param name="geometry">Optional geometry for the feature</param>
    /// <param name="links">Optional links for the feature</param>
    /// <returns>OGC GeoJSON feature</returns>
    public static GeoJsonFeature ToOgcGeoJsonFeature(this GeoJsonFeatureBase featureBase,
        SimpleGeoJsonGeometry? geometry = null,
        ImmutableArray<Link>? links = null)
    {
        // Convert ID to long if possible for OGC compatibility
        long? id = featureBase.Id switch
        {
            long longId => longId,
            int intId => intId,
            string strId when long.TryParse(strId, out var parsedId) => parsedId,
            _ => null
        };

        return new()
        {
            Type = "Feature",
            Id = id,
            Properties = featureBase.Properties.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            Geometry = geometry,
            Links = links
        };
    }

    /// <summary>
    /// Converts OGC GeoJsonFeature to shared GeoJsonFeatureBase
    /// </summary>
    /// <param name="ogcFeature">OGC GeoJSON feature</param>
    /// <returns>Shared GeoJSON feature base</returns>
    public static GeoJsonFeatureBase ToGeoJsonFeatureBase(this GeoJsonFeature ogcFeature)
        => GeoJsonFeatureBase.Create(
            ogcFeature.Id,
            ogcFeature.Properties.AsReadOnly(),
            ogcFeature.Geometry != null);

    /// <summary>
    /// Converts a shared GeoJsonGeometryBase to OGC SimpleGeoJsonGeometry
    /// </summary>
    /// <param name="geometryBase">Shared GeoJSON geometry base</param>
    /// <param name="coordinatesJson">Coordinates as JSON string</param>
    /// <param name="geometriesJson">Geometries as JSON string (for collections)</param>
    /// <returns>OGC simple GeoJSON geometry</returns>
    public static SimpleGeoJsonGeometry ToSimpleGeoJsonGeometry(this GeoJsonGeometryBase geometryBase,
        string? coordinatesJson = null,
        string? geometriesJson = null)
        => new()
        {
            Type = geometryBase.Type,
            CoordinatesJson = coordinatesJson,
            GeometriesJson = geometriesJson
        };

    /// <summary>
    /// Converts OGC SimpleGeoJsonGeometry to shared GeoJsonGeometryBase
    /// </summary>
    /// <param name="simpleGeometry">OGC simple GeoJSON geometry</param>
    /// <returns>Shared GeoJSON geometry base</returns>
    public static GeoJsonGeometryBase ToGeoJsonGeometryBase(this SimpleGeoJsonGeometry simpleGeometry)
        => GeoJsonGeometryBase.Create(
            simpleGeometry.Type,
            !string.IsNullOrEmpty(simpleGeometry.CoordinatesJson) || !string.IsNullOrEmpty(simpleGeometry.GeometriesJson));

    /// <summary>
    /// Converts a shared PagedResponseBase to OGC FeatureCollection properties
    /// </summary>
    /// <param name="pagedBase">Shared paged response base</param>
    /// <returns>Tuple of number matched and number returned properties</returns>
    public static (long? NumberMatched, int NumberReturned) ToFeatureCollectionProperties(this PagedResponseBase pagedBase)
        => (pagedBase.TotalCount, pagedBase.ReturnedCount);

    /// <summary>
    /// Creates a PagedResponseBase from OGC FeatureCollection properties
    /// </summary>
    /// <param name="numberMatched">Total number of features matched</param>
    /// <param name="numberReturned">Number of features returned</param>
    /// <returns>Shared paged response base</returns>
    public static PagedResponseBase ToPagedResponseBase(long? numberMatched, int numberReturned)
        => PagedResponseBase.Create(numberReturned, numberMatched);

    /// <summary>
    /// Creates SRID from OGC CRS string
    /// </summary>
    /// <param name="crs">OGC CRS string</param>
    /// <returns>SRID integer</returns>
    public static int ToSrid(this string crs)
        => ExtentExtensions.ExtractSridFromCrs(crs);

    /// <summary>
    /// Converts SRID to OGC CRS URI
    /// </summary>
    /// <param name="srid">Spatial Reference System ID</param>
    /// <returns>OGC CRS URI</returns>
    public static string ToOgcCrs(this int srid)
        => ExtentExtensions.ToOgcCrsUri(srid);

    /// <summary>
    /// Converts a shared SpatialReference to OGC CRS string
    /// </summary>
    /// <param name="spatialRef">Shared spatial reference</param>
    /// <returns>OGC CRS URI string</returns>
    public static string ToOgcCrs(this SpatialReference spatialRef)
        => ExtentExtensions.ToOgcCrsUri(spatialRef.ToSrid());

    /// <summary>
    /// Creates a shared SpatialReference from OGC CRS string
    /// </summary>
    /// <param name="crs">OGC CRS string</param>
    /// <returns>Shared spatial reference</returns>
    public static SpatialReference ToSpatialReference(this string crs)
        => SpatialReference.Create(ExtentExtensions.ExtractSridFromCrs(crs));
}

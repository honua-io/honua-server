// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Server.Features.FeatureServer.Models;

/// <summary>
/// Extension methods for converting between FeatureServer models and shared components
/// </summary>
public static class FeatureServerExtensions
{
    /// <summary>
    /// Converts a shared SpatialReference to FeatureServer SpatialReferenceInfo
    /// </summary>
    /// <param name="spatialRef">Shared spatial reference</param>
    /// <returns>FeatureServer spatial reference info</returns>
    public static SpatialReferenceInfo ToSpatialReferenceInfo(this SpatialReference spatialRef)
        => new()
        {
            Wkid = spatialRef.Wkid,
            LatestWkid = spatialRef.LatestWkid,
            VcsWkid = spatialRef.VcsWkid,
            LatestVcsWkid = spatialRef.LatestVcsWkid,
            Wkt = spatialRef.Wkt
        };

    /// <summary>
    /// Converts FeatureServer SpatialReferenceInfo to shared SpatialReference
    /// </summary>
    /// <param name="spatialRefInfo">FeatureServer spatial reference info</param>
    /// <returns>Shared spatial reference</returns>
    public static SpatialReference ToSpatialReference(this SpatialReferenceInfo spatialRefInfo)
        => SpatialReference.Create(
            spatialRefInfo.Wkid,
            spatialRefInfo.LatestWkid,
            spatialRefInfo.VcsWkid,
            spatialRefInfo.LatestVcsWkid,
            spatialRefInfo.Wkt);

    /// <summary>
    /// Converts a shared SpatialReference to FeatureServer GeoServicesSpatialReference
    /// </summary>
    /// <param name="spatialRef">Shared spatial reference</param>
    /// <returns>FeatureServer GeoServices spatial reference</returns>
    public static GeoServicesSpatialReference ToGeoServicesSpatialReference(this SpatialReference spatialRef)
        => new()
        {
            Wkid = spatialRef.Wkid,
            LatestWkid = spatialRef.LatestWkid,
            Wkt = spatialRef.Wkt
        };

    /// <summary>
    /// Converts FeatureServer GeoServicesSpatialReference to shared SpatialReference
    /// </summary>
    /// <param name="geoServicesSpatialRef">FeatureServer GeoServices spatial reference</param>
    /// <returns>Shared spatial reference</returns>
    public static SpatialReference ToSpatialReference(this GeoServicesSpatialReference geoServicesSpatialRef)
        => SpatialReference.Create(
            geoServicesSpatialRef.Wkid ?? 0,
            geoServicesSpatialRef.LatestWkid,
            vcsWkid: null,
            latestVcsWkid: null,
            wkt: geoServicesSpatialRef.Wkt);

    /// <summary>
    /// Converts nullable FeatureServer GeoServicesSpatialReference to nullable shared SpatialReference
    /// </summary>
    /// <param name="geoServicesSpatialRef">FeatureServer GeoServices spatial reference</param>
    /// <returns>Nullable shared spatial reference</returns>
    public static SpatialReference? ToNullableSpatialReference(this GeoServicesSpatialReference? geoServicesSpatialRef)
        => geoServicesSpatialRef?.ToSpatialReference();

    /// <summary>
    /// Converts nullable shared SpatialReference to nullable FeatureServer GeoServicesSpatialReference
    /// </summary>
    /// <param name="spatialRef">Nullable shared spatial reference</param>
    /// <returns>Nullable FeatureServer GeoServices spatial reference</returns>
    public static GeoServicesSpatialReference? ToNullableGeoServicesSpatialReference(this SpatialReference? spatialRef)
        => spatialRef?.ToGeoServicesSpatialReference();

    /// <summary>
    /// Converts a FeatureExtent to FeatureServer ExtentInfo
    /// </summary>
    /// <param name="extent">Feature extent</param>
    /// <returns>FeatureServer extent info</returns>
    public static ExtentInfo ToExtentInfo(this FeatureExtent extent)
        => new()
        {
            Xmin = extent.MinX,
            Ymin = extent.MinY,
            Xmax = extent.MaxX,
            Ymax = extent.MaxY,
            SpatialReference = extent.GetSpatialReference().ToSpatialReferenceInfo()
        };

    /// <summary>
    /// Converts FeatureServer ExtentInfo to FeatureExtent
    /// </summary>
    /// <param name="extentInfo">FeatureServer extent info</param>
    /// <returns>Feature extent</returns>
    public static FeatureExtent ToFeatureExtent(this ExtentInfo extentInfo)
        => FeatureExtent.Create(
            extentInfo.Xmin,
            extentInfo.Ymin,
            extentInfo.Xmax,
            extentInfo.Ymax,
            extentInfo.SpatialReference.ToSpatialReference().ToSrid());

    /// <summary>
    /// Converts a shared ServiceError to FeatureServer EditError
    /// </summary>
    /// <param name="serviceError">Shared service error</param>
    /// <returns>FeatureServer edit error</returns>
    public static EditError ToEditError(this ServiceError serviceError)
        => new()
        {
            Code = serviceError.ToNumericErrorCode(),
            Description = serviceError.Message
        };

    /// <summary>
    /// Converts FeatureServer EditError to shared ServiceError
    /// </summary>
    /// <param name="editError">FeatureServer edit error</param>
    /// <returns>Shared service error</returns>
    public static ServiceError ToServiceError(this EditError editError)
        => ServiceError.Create(editError.Code, editError.Description);

    /// <summary>
    /// Converts a shared GeoJsonFeatureBase to FeatureServer GeoJsonFeature
    /// </summary>
    /// <param name="featureBase">Shared GeoJSON feature base</param>
    /// <param name="geometry">Optional geometry for the feature</param>
    /// <returns>FeatureServer GeoJSON feature</returns>
    public static GeoJsonFeature ToGeoJsonFeature(this GeoJsonFeatureBase featureBase, GeoJsonGeometry? geometry = null)
        => new()
        {
            Type = "Feature",
            Id = featureBase.Id,
            Properties = featureBase.Properties.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            Geometry = geometry
        };

    /// <summary>
    /// Converts FeatureServer GeoJsonFeature to shared GeoJsonFeatureBase
    /// </summary>
    /// <param name="geoJsonFeature">FeatureServer GeoJSON feature</param>
    /// <returns>Shared GeoJSON feature base</returns>
    public static GeoJsonFeatureBase ToGeoJsonFeatureBase(this GeoJsonFeature geoJsonFeature)
        => GeoJsonFeatureBase.Create(
            geoJsonFeature.Id,
            geoJsonFeature.Properties.AsReadOnly(),
            geoJsonFeature.Geometry != null);

    /// <summary>
    /// Converts a shared PagedResponseBase to properties for FeatureServer QueryResponse
    /// </summary>
    /// <param name="pagedBase">Shared paged response base</param>
    /// <returns>Tuple of count and exceeded transfer limit properties</returns>
    public static (long? Count, bool ExceededTransferLimit) ToQueryResponseProperties(this PagedResponseBase pagedBase)
        => (pagedBase.TotalCount, pagedBase.ExceededTransferLimit);

    /// <summary>
    /// Creates a PagedResponseBase from FeatureServer QueryResponse properties
    /// </summary>
    /// <param name="queryResponse">FeatureServer query response</param>
    /// <returns>Shared paged response base</returns>
    public static PagedResponseBase ToPagedResponseBase(this QueryResponse queryResponse)
        => PagedResponseBase.Create(
            queryResponse.Features?.Length ?? 0,
            queryResponse.Count,
            queryResponse.ExceededTransferLimit);
}

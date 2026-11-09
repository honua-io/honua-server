// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

public static class FeatureServerExtensions
{
    public static SpatialReferenceInfo ToSpatialReferenceInfo(this SpatialReference spatialRef)
        => new()
        {
            Wkid = spatialRef.Wkid,
            LatestWkid = spatialRef.LatestWkid,
            VcsWkid = spatialRef.VcsWkid,
            LatestVcsWkid = spatialRef.LatestVcsWkid,
            Wkt = spatialRef.Wkt
        };

    public static SpatialReference ToSpatialReference(this SpatialReferenceInfo spatialRefInfo)
        => SpatialReference.Create(
            spatialRefInfo.Wkid,
            spatialRefInfo.LatestWkid,
            spatialRefInfo.VcsWkid,
            spatialRefInfo.LatestVcsWkid,
            spatialRefInfo.Wkt);

    public static ExtentInfo ToExtentInfo(this FeatureExtent extent)
        => new()
        {
            Xmin = extent.MinX,
            Ymin = extent.MinY,
            Xmax = extent.MaxX,
            Ymax = extent.MaxY,
            SpatialReference = extent.GetSpatialReference().ToSpatialReferenceInfo()
        };

    public static FeatureExtent ToFeatureExtent(this ExtentInfo extentInfo)
        => FeatureExtent.Create(
            extentInfo.Xmin,
            extentInfo.Ymin,
            extentInfo.Xmax,
            extentInfo.Ymax,
            extentInfo.SpatialReference.ToSpatialReference().ToSrid());

    public static ServiceError ToServiceError(this EditError editError)
        => ServiceError.Create(editError.Code, editError.Description);

    public static GeoJsonFeature ToGeoJsonFeature(this GeoJsonFeatureBase featureBase, GeoJsonGeometry? geometry = null)
        => new()
        {
            Type = "Feature",
            Id = featureBase.Id,
            Properties = featureBase.Properties.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            Geometry = geometry
        };

    public static GeoJsonFeatureBase ToGeoJsonFeatureBase(this GeoJsonFeature geoJsonFeature)
        => GeoJsonFeatureBase.Create(
            geoJsonFeature.Id,
            geoJsonFeature.Properties.AsReadOnly(),
            geoJsonFeature.Geometry != null);

    public static PagedResponseBase ToPagedResponseBase(this QueryResponse queryResponse)
        => PagedResponseBase.Create(
            queryResponse.Features?.Length ?? 0,
            queryResponse.Count,
            queryResponse.ExceededTransferLimit);
}

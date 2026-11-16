// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

internal interface IGeometryProcessor
{
    string GetGeometrySelectExpression(GeometryStorageType storageType, FeatureQuery query);

    string GetGeometryGmlExpression(GeometryStorageType storageType, FeatureQuery query);

    string GetGeometryGeoJsonExpression(GeometryStorageType storageType, FeatureQuery query);

    string GetGeometryKmlExpression(GeometryStorageType storageType, FeatureQuery query);

    string GetGeometryWriteExpression(GeometryStorageType storageType, string parameterName, int? layerSrid);

    string GetGeometryOperand(GeometryStorageType storageType, string? columnExpression = null, int? layerSrid = null);

    string BuildSpatialFilterGeometryExpression(SpatialFilter filter, FeatureQuery query, ref int paramIndex);

    double ConvertDistanceToMeters(double distance, DistanceUnit unit);

    string GetGeographyOperand(GeometryStorageType storageType, int? layerSrid);
}

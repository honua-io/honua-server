// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using CoreGeometryStorageType = Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType;

namespace Honua.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureQueryBuilder
{
    private void AppendKnnOrdering(
        StringBuilder sql,
        bool isKnnQuery,
        SpatialFilter? spatialFilter,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType,
        ref int paramIndex)
    {
        if (!isKnnQuery || !spatialFilter.HasValue)
        {
            return;
        }

        if (ShouldUseGeodesicKnn(geometryStorageType, query))
        {
            var geographyOperand = ((GeometryProcessor)_geometryProcessor).GetGeographyOperand(geometryStorageType, query.SpatialReferenceSrid);
            var filterGeometry = BuildGeographyFilterExpression(spatialFilter.Value, query, ref paramIndex);
            sql.Append(CultureInfo.InvariantCulture, $" ORDER BY ST_Distance({geographyOperand}, {filterGeometry})");
            return;
        }

        var geometryOperand = _geometryProcessor.GetGeometryOperand(geometryStorageType, layerSrid: query.SpatialReferenceSrid);
        var filterGeometryPlanar = _geometryProcessor.BuildSpatialFilterGeometryExpression(spatialFilter.Value, query, ref paramIndex);
        sql.Append(CultureInfo.InvariantCulture, $" ORDER BY {geometryOperand} <-> {filterGeometryPlanar}");
    }

    private static bool ShouldUseGeodesicKnn(CoreGeometryStorageType geometryStorageType, FeatureQuery query)
    {
        if (geometryStorageType == CoreGeometryStorageType.Geography)
        {
            return true;
        }

        if (!query.SpatialReferenceSrid.HasValue)
        {
            return false;
        }

        return IsLikelyGeographicSrid(query.SpatialReferenceSrid.Value);
    }

    private static bool IsLikelyGeographicSrid(int srid)
    {
        return srid == SpatialReference.WGS84.Wkid ||
               srid == 4269 ||
               srid == 4267 ||
               srid is >= 4000 and <= 4999;
    }
}

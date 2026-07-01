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
            var geographyOperand = _geometryProcessor.GetGeographyOperand(geometryStorageType, query.SpatialReferenceSrid);
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

        // Use the curated canonical geographic-SRID list (single source of truth) rather
        // than a loose 4000-4999 range, which swept in projected/geocentric CRS in that band
        // (e.g. EPSG:4978 geocentric) and missed geographic CRS outside it.
        return DistanceConversions.IsGeographicSrid(query.SpatialReferenceSrid.Value);
    }
}

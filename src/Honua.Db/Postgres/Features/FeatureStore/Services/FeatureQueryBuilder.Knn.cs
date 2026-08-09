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

        // The curated canonical geographic-SRID list is the primary source of truth. Until the
        // classifier unification (#2732) replaces this heuristic, add a safety net: an SRID in the
        // EPSG geographic 2D range (4000-4999) that is not on the allowlist is still a lat/lon
        // degree CRS (e.g. 4674 SIRGAS 2000, 4490 CGCS2000). Planar degree KNN on those would
        // produce meaningless distances, so route them through the same geodesic path as 4326.
        // The narrow 4xxx geocentric/3D codes (4978/4979) are out of scope for 2D KNN.
        var srid = query.SpatialReferenceSrid.Value;
        return DistanceConversions.IsGeographicSrid(srid) || IsUnlistedGeographicSridRange(srid);
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Postgres.Features.Infrastructure;

namespace Honua.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureQueryBuilder
{
    // Branch-versioning overlay read (#1272 Track B, ADR-0051). The DEFAULT version emits NOTHING extra
    // (today's exact SQL FROM the base features table) — this is the CITE firewall. A non-DEFAULT version
    // replaces the base table reference with an overlay subquery that produces the identical column shape
    // (objectid, layer_id, geometry, attributes) as the base table so the surrounding SELECT/WHERE is
    // unchanged: (DEFAULT base minus the OIDs the branch shadows) UNION ALL (overlay non-delete rows).
    //
    // The overlay is only wired for the primary non-spatial-parameter read shapes (select/count/objectIds
    // and the GeoServices point/raw paths) whose feature source is `FROM {_tableName} WHERE layer_id = $1`
    // and whose query parameters are bound positionally from the builder's `parameters` list. KNN /
    // distance / analytics / H3 / bins / MVT / encoded-binary versioned reads are intentionally not
    // overlaid in v1 (see VersionedReadsNotSupported); they throw a clear NotSupported so no half-wired
    // versioned shape silently returns DEFAULT data.

    private const string VersionEditsTable = "honua.version_edits";

    /// <summary>
    /// Returns the feature-source SQL to substitute for the base table reference. For the DEFAULT version
    /// this is the bare base table name, so the emitted SQL is byte-identical to the non-versioned path.
    /// For a branch version it is a parenthesized overlay subquery aliased to <paramref name="alias"/>
    /// that yields the same columns as the base table. The overlay's version parameter is appended to
    /// <paramref name="parameters"/> in positional order at the point the source is emitted.
    /// </summary>
    private string BuildVersionedFeatureSource(
        FeatureQuery query,
        string alias,
        ref int paramIndex,
        List<object> parameters)
    {
        var version = query.VersionContext;
        if (version is null || version.Value.IsDefault)
        {
            return _tableName;
        }

        // KNN / returnDistance reads bind the distance geometry ahead of the WHERE parameters
        // (FeatureDataAccess.AddQueryParameters), an ordering the overlay's prepended version parameter
        // would collide with. These spatial-parameter shapes are not overlaid in v1; throw rather than
        // emit a versioned query whose parameters would bind out of order.
        if (query.SpatialFilter is { } spatial &&
            (spatial.SpatialRelationship == SpatialRelationship.NearestNeighbor || spatial.ReturnDistance))
        {
            throw new NotSupportedException(
                "Branch-versioned KNN/returnDistance reads are not supported in this release. " +
                "Use the DEFAULT version for nearest-neighbor or distance-returning queries.");
        }

        var versionId = version.Value.VersionId
            ?? throw new InvalidOperationException("Non-default version context is missing a version id.");

        var versionParamIndex = paramIndex++;
        parameters.Add(versionId);
        var versionParam = $"${versionParamIndex}";

        var objectId = DatabaseSchema.ObjectIdColumn;
        var layerId = DatabaseSchema.LayerIdColumn;
        var geometry = DatabaseSchema.GeometryColumn;
        var attributes = DatabaseSchema.AttributesColumn;

        return string.Create(CultureInfo.InvariantCulture,
            $"(SELECT b.{objectId}, b.{layerId}, b.{geometry}, b.{attributes} " +
            $"FROM {_tableName} b " +
            $"WHERE NOT EXISTS (SELECT 1 FROM {VersionEditsTable} ve " +
            $"WHERE ve.version_id = {versionParam} AND ve.{layerId} = b.{layerId} AND ve.{objectId} = b.{objectId}) " +
            $"UNION ALL " +
            $"SELECT o.{objectId}, o.{layerId}, o.{geometry}, o.{attributes} " +
            $"FROM {VersionEditsTable} o " +
            $"WHERE o.version_id = {versionParam} AND o.operation <> 3) AS {alias}");
    }

    /// <summary>
    /// Whether the query targets a non-DEFAULT branch version. DEFAULT (null/IsDefault) always returns
    /// false so the byte-identical base path is taken.
    /// </summary>
    private static bool IsVersionedQuery(FeatureQuery query)
        => query.VersionContext is { IsDefault: false };

    /// <summary>
    /// Guards a query shape that does not support the v1 branch-versioning overlay. Throws for a
    /// non-DEFAULT version; no-op for DEFAULT so the base path is unaffected.
    /// </summary>
    private static void GuardVersionedReadSupported(FeatureQuery query, string shape)
    {
        if (IsVersionedQuery(query))
        {
            throw new NotSupportedException(
                $"Branch-versioned reads are not supported for the '{shape}' query shape in this release. " +
                "Use the DEFAULT version for this operation.");
        }
    }

    /// <summary>
    /// Nullable overload of <see cref="GuardVersionedReadSupported(FeatureQuery, string)"/> for builder
    /// entry points that accept an optional query. A null query is DEFAULT (no guard).
    /// </summary>
    private static void GuardVersionedReadSupported(FeatureQuery? query, string shape)
    {
        if (query.HasValue)
        {
            GuardVersionedReadSupported(query.Value, shape);
        }
    }
}

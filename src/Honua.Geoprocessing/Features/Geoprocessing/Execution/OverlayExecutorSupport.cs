// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using NetTopologySuite.Features;
using NetTopologySuite.Operation.Union;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// Shared helpers for the managed (NetTopologySuite) vector overlay/proximity and
/// statistics tool packs (#2139, #2140, #2206). Every executor in those packs is a
/// <see cref="FeatureCollectionTransformExecutor"/> subclass that reads one or more
/// inline <c>data:application/geo+json;base64</c> FeatureCollections, runs an
/// in-memory geometry/attribute computation, and publishes a FeatureCollection
/// back. Table outputs are modelled as a FeatureCollection of null-geometry
/// features (one feature per table row), so they flow through the same canonical
/// artifact envelope without a second encoder — mirroring how
/// <c>generalization.dissolve</c> declares <see cref="Honua.Core.Features.Geoprocessing.Domain.ArtifactKind.Table"/>
/// while reusing the shared GeoJSON writer.
/// </summary>
internal static class OverlayExecutorSupport
{
    /// <summary>
    /// Reads a required secondary FeatureCollection input (e.g. the overlay/clip/
    /// erase/near layer) supplied as a data URI under <paramref name="name"/>.
    /// Throws <see cref="TransformInputException"/> for a missing or malformed
    /// value so the executor surfaces a classified job failure.
    /// </summary>
    public static FeatureCollection ReadLayer(StepInputReader inputs, string name, long maxBytes)
    {
        var uri = inputs.Require(name);
        if (!FeatureCollectionArtifact.TryParseDataUri(uri, out var features, out var error, maxBytes))
        {
            throw new TransformInputException($"'{name}' {error}");
        }

        return features;
    }

    /// <summary>
    /// Computes the cascaded union of every non-null, non-empty geometry in the
    /// supplied feature set. Returns <c>null</c> when no usable geometry exists.
    /// </summary>
    public static NtsGeometry? UnionGeometry(FeatureCollection features)
    {
        var geometries = features
            .Select(feature => feature.Geometry)
            .Where(geometry => geometry is not null && !geometry.IsEmpty)
            .Select(geometry => geometry!)
            .ToList();

        return geometries.Count switch
        {
            0 => null,
            1 => geometries[0],
            _ => CascadedPolygonUnionOrCollection(geometries),
        };
    }

    private static NtsGeometry CascadedPolygonUnionOrCollection(IReadOnlyList<NtsGeometry> geometries)
    {
        // CascadedPolygonUnion only handles (multi)polygons; for mixed/linear/point
        // inputs fall back to the generic UnaryUnionOp which preserves dimension.
        var allPolygonal = geometries.All(geometry => geometry.OgcGeometryType is
            NetTopologySuite.Geometries.OgcGeometryType.Polygon
            or NetTopologySuite.Geometries.OgcGeometryType.MultiPolygon);

        if (allPolygonal)
        {
            return new CascadedPolygonUnion(geometries.ToList()).Union();
        }

        return UnaryUnionOp.Union(geometries.ToList());
    }

    /// <summary>
    /// Copies the attribute values of <paramref name="source"/> into a fresh
    /// <see cref="AttributesTable"/>, optionally prefixing every key. A null
    /// attribute table yields an empty table.
    /// </summary>
    public static AttributesTable CopyAttributes(IFeature source, string? prefix = null)
    {
        var table = new AttributesTable();
        if (source.Attributes is null)
        {
            return table;
        }

        foreach (var name in source.Attributes.GetNames())
        {
            var key = string.IsNullOrEmpty(prefix) ? name : prefix + name;
            Upsert(table, key, source.Attributes.GetOptionalValue(name));
        }

        return table;
    }

    /// <summary>
    /// Merges every attribute from <paramref name="other"/> into
    /// <paramref name="target"/>, prefixing keys that already exist (or always when
    /// <paramref name="prefix"/> is supplied) to avoid clobbering.
    /// </summary>
    public static void MergeAttributes(AttributesTable target, IFeature other, string prefix)
    {
        if (other.Attributes is null)
        {
            return;
        }

        foreach (var name in other.Attributes.GetNames())
        {
            var key = target.Exists(name) ? prefix + name : name;
            Upsert(target, key, other.Attributes.GetOptionalValue(name));
        }
    }

    /// <summary>Inserts or overwrites a value on an attribute table by name.</summary>
    public static void Upsert(AttributesTable table, string name, object? value)
    {
        if (table.Exists(name))
        {
            table[name] = value;
        }
        else
        {
            table.Add(name, value);
        }
    }

    /// <summary>
    /// Builds a null-geometry table row from the supplied ordered name/value pairs.
    /// Used by the table-emitting tools (near-table, frequency, summarize,
    /// calculate-statistics).
    /// </summary>
    public static Feature TableRow(IEnumerable<(string Name, object? Value)> values)
    {
        var table = new AttributesTable();
        foreach (var (name, value) in values)
        {
            Upsert(table, name, value);
        }

        return new Feature(null, table);
    }
}

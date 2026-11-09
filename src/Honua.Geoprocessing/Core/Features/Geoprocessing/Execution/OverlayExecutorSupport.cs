// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using NetTopologySuite.Features;
using NetTopologySuite.Operation.Union;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Geoprocessing.Execution;

internal static class OverlayExecutorSupport
{
    public static FeatureCollection ReadLayer(StepInputReader inputs, string name, long maxBytes)
    {
        var uri = inputs.Require(name);
        if (!FeatureCollectionArtifact.TryParseDataUri(uri, out var features, out var error, maxBytes))
        {
            throw new TransformInputException($"'{name}' {error}");
        }

        return features;
    }

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

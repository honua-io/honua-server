// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Server.Features.Infrastructure.Rendering;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;

namespace Honua.Server.Features.Geoprocessing.Execution;

/// <summary>
/// <c>transform.reproject</c> executor. Reprojects every feature's geometry between
/// SRIDs on the lean, GDAL-free path, reusing the trunk in-memory
/// <see cref="CoordinateTransformer"/> (identity, Web Mercator aliases, and
/// WGS 84 (4326) ↔ Web Mercator). Datum-shift pairs that require ST_Transform / PROJ
/// are deferred to the native worker profile and rejected here with a clear error,
/// matching <c>geometry.project</c>. Attributes are carried through. Reconciled from
/// the GeoETL baseline ReprojectTransform onto the #1185 process/executor contract;
/// the managed math is replaced by the shared CoordinateTransformer so this transform
/// and geometry.project stay bit-for-bit aligned.
/// </summary>
internal sealed class ReprojectTransformExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    internal const string HandledProcessId = "transform.reproject";

    private static readonly HashSet<int> WebMercatorAliases =
        new() { 3857, 900913, 102100, 102113, 3785 };

    protected override string ProcessId => HandledProcessId;

    protected override List<IFeature> Apply(
        FeatureCollection source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var fromSrid = ReadSrid(inputs, "fromSrid");
        var toSrid = ReadSrid(inputs, "toSrid");

        if (!IsTransformSupported(fromSrid, toSrid))
        {
            throw new TransformInputException(
                $"reproject from SRID {fromSrid} to {toSrid} is not supported by the managed transform path. " +
                "Supported: identity, Web Mercator aliases (3857/900913/102100/102113/3785), and " +
                "WGS 84 (4326) ↔ Web Mercator. Datum-shift pairs require the native worker profile.");
        }

        var passthrough = fromSrid == toSrid
            || (WebMercatorAliases.Contains(fromSrid) && WebMercatorAliases.Contains(toSrid));

        var output = new List<IFeature>(source.Count);
        foreach (var feature in source)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var geometry = feature.Geometry;
            if (geometry is null || geometry.IsEmpty)
            {
                output.Add(feature);
                continue;
            }

            Geometry projected;
            if (passthrough)
            {
                projected = geometry.Copy();
            }
            else
            {
                var editor = new GeometryEditor(geometry.Factory);
                projected = editor.Edit(geometry, new CoordinateOperation(fromSrid, toSrid));
            }

            projected.SRID = toSrid;
            output.Add(new Feature(projected, feature.Attributes));
        }

        return output;
    }

    private static bool IsTransformSupported(int fromSrid, int toSrid)
    {
        if (fromSrid == toSrid)
        {
            return true;
        }

        if (WebMercatorAliases.Contains(fromSrid) && WebMercatorAliases.Contains(toSrid))
        {
            return true;
        }

        if (fromSrid == 4326 && WebMercatorAliases.Contains(toSrid))
        {
            return true;
        }

        return WebMercatorAliases.Contains(fromSrid) && toSrid == 4326;
    }

    private static int ReadSrid(StepInputReader inputs, string key)
    {
        if (!inputs.TryGet(key, out var raw)
            || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var srid)
            || srid <= 0)
        {
            throw new TransformInputException($"missing or invalid input '{key}'; expected a positive integer");
        }

        return srid;
    }

    private sealed class CoordinateOperation(int fromSrid, int toSrid) : GeometryEditor.CoordinateOperation
    {
        public override Coordinate[] Edit(Coordinate[] coordinates, Geometry geometry)
        {
            ArgumentNullException.ThrowIfNull(coordinates);
            var transformed = new Coordinate[coordinates.Length];
            for (var i = 0; i < coordinates.Length; i++)
            {
                var original = coordinates[i];
                var (x, y) = CoordinateTransformer.TransformPoint(original.X, original.Y, fromSrid, toSrid);
                transformed[i] = new Coordinate(x, y);
            }

            return transformed;
        }
    }
}

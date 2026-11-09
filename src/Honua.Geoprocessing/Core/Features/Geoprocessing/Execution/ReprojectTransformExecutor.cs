// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Runtime.CompilerServices;
using Honua.Infrastructure.Rendering;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;

namespace Honua.Geoprocessing.Execution;

internal sealed class ReprojectTransformExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    internal const string HandledProcessId = "transform.reproject";

    protected override string ProcessId => HandledProcessId;

    // NOTE: managed accept-set vs. native-escalation are kept in lock-step by the
    // shared ManagedReprojectFastPath predicate. This executor rejects any pair the
    // submit path should have escalated to the native worker; the submit path
    // escalates exactly the pairs this executor rejects.
    protected override async IAsyncEnumerable<IFeature> ApplyStream(
        IAsyncEnumerable<IFeature> source,
        StepInputReader inputs,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var fromSrid = ReadSrid(inputs, "fromSrid");
        var toSrid = ReadSrid(inputs, "toSrid");

        if (!ManagedReprojectFastPath.IsManagedFastPath(fromSrid, toSrid))
        {
            throw new TransformInputException(
                $"reproject from SRID {fromSrid} to {toSrid} is not supported by the managed transform path. " +
                "Supported: identity, Web Mercator aliases (3857/900913/102100/102113/3785), and " +
                "WGS 84 (4326) ↔ Web Mercator. Datum-shift pairs require the native worker profile.");
        }

        var passthrough = ManagedReprojectFastPath.IsPassthrough(fromSrid, toSrid);

        await foreach (var feature in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var geometry = feature.Geometry;
            if (geometry is null)
            {
                yield return feature;
                continue;
            }

            if (geometry.IsEmpty)
            {
                // Empty geometries carry no coordinates to transform, but they
                // still move to the target CRS: stamp toSrid like the non-empty
                // path instead of leaking the source srid downstream (#2744).
                if (geometry.SRID == toSrid)
                {
                    yield return feature;
                }
                else
                {
                    var stampedEmpty = geometry.Copy();
                    stampedEmpty.SRID = toSrid;
                    yield return new Feature(stampedEmpty, feature.Attributes);
                }

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
                projected = editor.Edit(geometry, new SridCoordinateOperation(fromSrid, toSrid));
            }

            projected.SRID = toSrid;
            yield return new Feature(projected, feature.Attributes);
        }
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

    // Named distinctly from the NTS base type (GeometryEditor.CoordinateOperation) it
    // overrides so the two are never ambiguous at a glance.
    private sealed class SridCoordinateOperation(int fromSrid, int toSrid) : GeometryEditor.CoordinateOperation
    {
        public override Coordinate[] Edit(Coordinate[] coordinates, Geometry geometry)
        {
            ArgumentNullException.ThrowIfNull(coordinates);
            var transformed = new Coordinate[coordinates.Length];
            for (var i = 0; i < coordinates.Length; i++)
            {
                var original = coordinates[i];
                var (x, y) = CoordinateTransformer.TransformPoint(original.X, original.Y, fromSrid, toSrid);

                // Copy the source coordinate to preserve its runtime dimension
                // (CoordinateZ / CoordinateM / CoordinateZM) and only overwrite the
                // horizontal ordinates; rebuilding a bare Coordinate would silently
                // drop Z/M through the transformed sequence (#2744).
                var projected = original.Copy();
                projected.X = x;
                projected.Y = y;
                transformed[i] = projected;
            }

            return transformed;
        }
    }
}

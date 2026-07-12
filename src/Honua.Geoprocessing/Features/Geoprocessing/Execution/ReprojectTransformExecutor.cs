// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Runtime.CompilerServices;
using Honua.Infrastructure.Rendering;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>transform.reproject</c> executor. Reprojects every feature's geometry between
/// SRIDs on the lean, GDAL-free path, reusing the trunk in-memory
/// <see cref="CoordinateTransformer"/> (identity, Web Mercator aliases, and
/// WGS 84 (4326) ↔ Web Mercator). Datum-shift pairs that require ST_Transform / PROJ
/// are deferred to the native worker profile and rejected here with a clear error,
/// matching <c>geometry.project</c>. Attributes are carried through. Reconciled from
/// the GeoETL baseline ReprojectTransform onto the #1185 process/executor contract;
/// the managed math is replaced by the shared CoordinateTransformer so this transform
/// and geometry.project stay bit-for-bit aligned. Streams: a per-feature map; SRIDs
/// are validated once before the stream is consumed (the validation surfaces on the
/// first pull as a classified <c>Invalid ... inputs</c> failure).
/// </summary>
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
                projected = editor.Edit(geometry, new CoordinateOperation(fromSrid, toSrid));
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

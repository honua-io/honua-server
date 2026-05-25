// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Runtime.CompilerServices;
using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Domain;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Core.Features.GeoETL.Services.Transforms;

/// <summary>
/// Phase 1 managed reproject transform. Reprojects each feature's geometry between
/// SRIDs on the lean, GDAL-free path: identity, Web Mercator aliases, and
/// WGS 84 (4326) ↔ Web Mercator (3857 and aliases). This mirrors the managed
/// in-memory transform the geoprocessing <c>geometry.project</c> executor uses.
/// Datum-shift pairs that require <c>ST_Transform</c> / PROJ are deferred to the
/// native worker profile (Child Ticket F) and are rejected here with a clear error.
/// </summary>
/// <remarks>
/// Required <see cref="TransformConfig.Options"/>: <c>fromSrid</c>, <c>toSrid</c>
/// (positive integers). Heavy spatial transforms continue to delegate to PostGIS
/// per ADR-0038; this transform exists so the common WGS 84 ↔ Web Mercator case runs
/// in-process without a database round-trip.
/// </remarks>
public sealed class ReprojectTransform : IPipelineTransform
{
    /// <summary>
    /// The transform type discriminator.
    /// </summary>
    public const string TransformType = "reproject";

    private static readonly int[] WebMercatorAliases = [3857, 900913, 102100, 102113, 3785];
    private const double EarthRadius = 6378137.0;

    /// <inheritdoc />
    public string Type => TransformType;

    /// <inheritdoc />
    public async IAsyncEnumerable<IFeature> TransformAsync(
        TransformConfig config,
        IAsyncEnumerable<IFeature> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(source);

        var fromSrid = ReadSrid(config, "fromSrid");
        var toSrid = ReadSrid(config, "toSrid");

        if (!IsSupported(fromSrid, toSrid))
        {
            throw new NotSupportedException(
                $"Reproject from SRID {fromSrid} to {toSrid} is not supported by the managed Phase 1 " +
                "transform path. Supported: identity, Web Mercator aliases, and WGS 84 (4326) ↔ Web Mercator. " +
                "Datum-shift transforms require the native worker profile (honua-worker-etl).");
        }

        await foreach (var feature in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (feature.Geometry is null || feature.Geometry.IsEmpty)
            {
                yield return feature;
                continue;
            }

            var projected = Reproject(feature.Geometry, fromSrid, toSrid);
            yield return new Feature(projected, feature.Attributes);
        }
    }

    private static NtsGeometry Reproject(NtsGeometry geometry, int fromSrid, int toSrid)
    {
        if (fromSrid == toSrid ||
            (Array.IndexOf(WebMercatorAliases, fromSrid) >= 0 && Array.IndexOf(WebMercatorAliases, toSrid) >= 0))
        {
            var copy = geometry.Copy();
            copy.SRID = toSrid;
            return copy;
        }

        Func<double, double, (double X, double Y)> transform =
            IsWgs84(fromSrid)
                ? LonLatToWebMercator
                : WebMercatorToLonLat;

        var filter = new DelegateCoordinateFilter(transform);
        var result = geometry.Copy();
        result.Apply(filter);
        result.GeometryChanged();
        result.SRID = toSrid;
        return result;
    }

    private static (double X, double Y) LonLatToWebMercator(double lon, double lat)
    {
        var x = EarthRadius * (lon * Math.PI / 180.0);
        var clampedLat = Math.Max(Math.Min(lat, 89.99), -89.99);
        var y = EarthRadius * Math.Log(Math.Tan((90.0 + clampedLat) * Math.PI / 360.0));
        return (x, y);
    }

    private static (double X, double Y) WebMercatorToLonLat(double x, double y)
    {
        var lon = (x / EarthRadius) * 180.0 / Math.PI;
        var lat = (2.0 * Math.Atan(Math.Exp(y / EarthRadius)) - Math.PI / 2.0) * 180.0 / Math.PI;
        return (lon, lat);
    }

    private static bool IsSupported(int fromSrid, int toSrid)
    {
        if (fromSrid == toSrid)
        {
            return true;
        }

        var fromMerc = Array.IndexOf(WebMercatorAliases, fromSrid) >= 0;
        var toMerc = Array.IndexOf(WebMercatorAliases, toSrid) >= 0;
        if (fromMerc && toMerc)
        {
            return true;
        }

        return (IsWgs84(fromSrid) && toMerc) || (fromMerc && IsWgs84(toSrid));
    }

    private static bool IsWgs84(int srid) => srid is 4326 or 4979 or 0;

    private static int ReadSrid(TransformConfig config, string key)
    {
        if (!config.Options.TryGetValue(key, out var raw) ||
            !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var srid) ||
            srid < 0)
        {
            throw new InvalidOperationException(
                $"Reproject transform requires a non-negative integer '{key}' option.");
        }

        return srid;
    }

    private sealed class DelegateCoordinateFilter(Func<double, double, (double X, double Y)> transform)
        : ICoordinateSequenceFilter
    {
        public bool Done => false;

        public bool GeometryChanged => true;

        public void Filter(CoordinateSequence seq, int i)
        {
            var (x, y) = transform(seq.GetX(i), seq.GetY(i));
            seq.SetOrdinate(i, Ordinate.X, x);
            seq.SetOrdinate(i, Ordinate.Y, y);
        }
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Raster.Domain;
using Honua.Infrastructure.Licensing;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Http;
using NetTopologySuite.Geometries;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

internal static class ImageServerMosaicHelpers
{
    /// <summary>
    /// Parses the Esri ImageServer <c>time</c> parameter: <c>time=&lt;timeInstant&gt;</c> or
    /// <c>time=&lt;startTime&gt;,&lt;endTime&gt;</c>, where each value is epoch milliseconds (the
    /// form the service advertises in <c>timeInfo.timeExtent</c>) or an ISO 8601 instant and either
    /// extent bound may be <c>null</c>/empty for an open interval. An instant is returned in
    /// <paramref name="timestamp"/> with a null <paramref name="timeStart"/>; an extent returns its
    /// end bound in <paramref name="timestamp"/> and its start bound in <paramref name="timeStart"/>.
    /// Shares <see cref="GeoServicesTemporalQueryBuilder.TryParseTimeParameter"/> with
    /// MapServer/FeatureServer so every GeoServices surface accepts the same values.
    /// </summary>
    internal static bool TryParseTime(
        string? value,
        out DateTimeOffset? timestamp,
        out DateTimeOffset? timeStart,
        out string? error)
    {
        timestamp = null;
        timeStart = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        // Esri clients send time=null (and the ArcGIS JS API sends an empty value) to mean
        // "no temporal filter / all times". Treat the literal token as the absence of a time
        // constraint rather than attempting to parse it as an instant.
        var trimmed = value.Trim();
        if (string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!GeoServicesTemporalQueryBuilder.TryParseTimeParameter(trimmed, out var start, out var end))
        {
            error = $"Invalid time value '{value}'. Use an epoch-millisecond or ISO 8601 instant, " +
                "or a '<startTime>,<endTime>' extent whose start is not after its end.";
            return false;
        }

        if (trimmed.Contains(','))
        {
            timeStart = start;
        }

        timestamp = end;
        return true;
    }

    internal static IResult? RequireTemporalMosaicAccess(HttpContext context, DateTimeOffset? timestamp, DateTimeOffset? timeStart = null)
    {
        if (!timestamp.HasValue && !timeStart.HasValue)
        {
            return null;
        }

        return LicenseGate.RequireEntitlement(
            context,
            "raster.temporal-mosaic",
            "Temporal raster mosaic");
    }

    internal static byte[] CreateEnvelopeGeometry(double minX, double minY, double maxX, double maxY)
    {
        var envelope = new Envelope(minX, maxX, minY, maxY);
        var factory = new GeometryFactory();
        return new NetTopologySuite.IO.WKBWriter().Write(factory.ToGeometry(envelope));
    }

    internal static byte[] CreatePointGeometry(double x, double y)
    {
        var factory = new GeometryFactory();
        return new NetTopologySuite.IO.WKBWriter().Write(factory.CreatePoint(new Coordinate(x, y)));
    }

    internal static RasterExtent? ComputeAggregateExtent(IEnumerable<RasterInfo> rasters)
    {
        var hasExtent = false;
        double xMin = double.MaxValue;
        double yMin = double.MaxValue;
        double xMax = double.MinValue;
        double yMax = double.MinValue;
        int? srid = null;

        foreach (var raster in rasters)
        {
            if (raster.Extent is not { } extent)
            {
                continue;
            }

            hasExtent = true;
            xMin = Math.Min(xMin, extent.XMin);
            yMin = Math.Min(yMin, extent.YMin);
            xMax = Math.Max(xMax, extent.XMax);
            yMax = Math.Max(yMax, extent.YMax);
            srid ??= extent.Srid;
        }

        if (!hasExtent)
        {
            return null;
        }

        return new RasterExtent
        {
            XMin = xMin,
            YMin = yMin,
            XMax = xMax,
            YMax = yMax,
            Srid = srid
        };
    }

    internal static long?[]? CreateTimeExtent(IEnumerable<RasterInfo> rasters)
    {
        var timestamps = rasters.Select(raster => raster.AcquisitionDate ?? raster.CreatedAt).ToArray();
        if (timestamps.Length == 0)
        {
            return null;
        }

        return [timestamps.Min().ToUnixTimeMilliseconds(), timestamps.Max().ToUnixTimeMilliseconds()];
    }

}

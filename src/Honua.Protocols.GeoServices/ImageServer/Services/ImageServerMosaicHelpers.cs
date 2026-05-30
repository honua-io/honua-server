// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Raster.Domain;
using Honua.Server.Features.Infrastructure.Licensing;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Http;
using NetTopologySuite.Geometries;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

internal static class ImageServerMosaicHelpers
{
    internal static bool TryParseTime(string? value, out DateTimeOffset? timestamp, out string? error)
    {
        timestamp = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (value.Contains(',') || value.Contains('/'))
        {
            error = "Only single instant timestamps are supported for raster temporal mosaics.";
            return false;
        }

        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            error = $"Invalid time value '{value}'. Use an ISO 8601 instant.";
            return false;
        }

        timestamp = parsed;
        return true;
    }

    internal static IResult? RequireTemporalMosaicAccess(HttpContext context, DateTimeOffset? timestamp)
    {
        if (!timestamp.HasValue)
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
        DateTimeOffset? min = null;
        DateTimeOffset? max = null;

        foreach (var raster in rasters)
        {
            var timestamp = raster.AcquisitionDate ?? raster.CreatedAt;
            min = min.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(Math.Min(min.Value.ToUnixTimeMilliseconds(), timestamp.ToUnixTimeMilliseconds())) : timestamp;
            max = max.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(max.Value.ToUnixTimeMilliseconds(), timestamp.ToUnixTimeMilliseconds())) : timestamp;
        }

        if (!min.HasValue || !max.HasValue)
        {
            return null;
        }

        return [min.Value.ToUnixTimeMilliseconds(), max.Value.ToUnixTimeMilliseconds()];
    }

}

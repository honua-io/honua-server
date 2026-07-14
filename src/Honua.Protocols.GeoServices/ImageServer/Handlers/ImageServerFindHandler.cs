// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Services;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices.ImageServer.Handlers;

/// <summary>
/// Handler for the ArcGIS ImageServer <c>find</c> operation.
/// </summary>
internal sealed class ImageServerFindHandler
{
    private const int DefaultMaxCount = 100;
    private const int MaxFindCount = 1000;

    private readonly record struct FindPoint(double X, double Y, double? Z, int? Srid);

    private readonly IMetadataV2GraphProvider _graphProvider;
    private readonly IImageServerCatalogReader _catalogReader;
    private readonly ILogger<ImageServerFindHandler> _logger;

    public ImageServerFindHandler(
        IMetadataV2GraphProvider graphProvider,
        IImageServerCatalogReader catalogReader,
        ILogger<ImageServerFindHandler> logger)
    {
        _graphProvider = graphProvider ?? throw new ArgumentNullException(nameof(graphProvider));
        _catalogReader = catalogReader ?? throw new ArgumentNullException(nameof(catalogReader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Finds catalog images whose footprints contain the requested target geometry.
    /// </summary>
    public async Task<IResult> FindAsync(
        HttpContext context,
        int layerId,
        IReadOnlyDictionary<string, StringValues> values,
        CancellationToken cancellationToken)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "find",
            HonuaTelemetry.Protocols.ImageServer,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.Operation, "find")
            .WithTag(HonuaTelemetry.Tags.LayerId, layerId.ToString(CultureInfo.InvariantCulture));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (ImageServerV2Lookups.FindByLayerIndex(snapshot, layerId) is null)
            {
                ImageServerLog.LayerNotFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "Layer not found.");
            }

            if (!TryParseFind(values, out var query, out var target, out var maxCount, out var parseError))
            {
                ImageServerLog.InvalidFindParameters(_logger, layerId, parseError ?? "Invalid find parameter.");
                return StandardErrorHelpers.CreateBadRequest(context, parseError ?? "Invalid find parameter.");
            }

            ImageServerCatalogPage page;
            try
            {
                page = await _catalogReader.ReadAsync(layerId, query, cancellationToken).ConfigureAwait(false);
            }
            catch (ImageServerCatalogFilterException ex)
            {
                ImageServerLog.InvalidFindParameters(_logger, layerId, ex.Message);
                return StandardErrorHelpers.CreateBadRequest(context, "Invalid ImageServer find filter.");
            }

            // Orientation-ranked selection (#1880): when any candidate carries exterior
            // orientation (off-nadir angle), prefer the most nadir-looking images at the target,
            // breaking ties by footprint distance. Falls back to pure distance ranking when no
            // candidate has orientation metadata (the common case for plain COGs).
            var useOrientationRanking = page.Items.Any(static item =>
                ImageServerSensorModel.TryReadOffNadirAngle(item.SensorMetadata) is not null);

            var ordered = useOrientationRanking
                ? page.Items
                    .OrderBy(item => OffNadirSortKey(item))
                    .ThenBy(item => DistanceSortKey(item, target))
                    .ThenBy(static item => item.ObjectId)
                : page.Items
                    .OrderBy(item => DistanceSortKey(item, target))
                    .ThenBy(static item => item.ObjectId);

            var images = ordered
                .Take(maxCount)
                .Select(BuildImage)
                .ToArray();

            stopwatch.Stop();
            ImageServerLog.FindCompleted(_logger, layerId, images.Length, stopwatch.Elapsed.TotalMilliseconds);
            scope.SetSuccess(images.Length);
            scope.CategorizeLatency(stopwatch.Elapsed.TotalMilliseconds);

            var response = new ImageServerFindResponse { Images = images };
            return Results.Json(response, ImageServerJsonContext.Default.ImageServerFindResponse);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Intentionally generic: this is a top-level protocol request handler; any
        // unexpected failure (parsing bugs, provider errors, etc.) must map to a
        // generic 500 rather than crash the host or leak internals to the client.
        catch (Exception ex)
        {
            ImageServerLog.FindFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "ImageServer find failed.");
        }
    }

    private static bool TryParseFind(
        IReadOnlyDictionary<string, StringValues> values,
        out ImageServerCatalogQuery query,
        out FindPoint target,
        out int maxCount,
        out string? error)
    {
        query = new ImageServerCatalogQuery();
        target = default;
        maxCount = DefaultMaxCount;
        error = null;

        var inSrRaw = GetString(values, "inSR") ?? GetString(values, "inSr");
        var inSrid = ImageServerGeometryHelpers.TryParseSrid(inSrRaw);
        if (!string.IsNullOrWhiteSpace(inSrRaw) && !inSrid.HasValue)
        {
            error = "Invalid inSR parameter.";
            return false;
        }

        if (!TryParsePoint(GetString(values, "toGeometry"), "toGeometry", inSrid, out target, out error))
        {
            return false;
        }

        var fromGeometry = GetString(values, "fromGeometry");
        if (!string.IsNullOrWhiteSpace(fromGeometry) &&
            !TryParsePoint(fromGeometry, "fromGeometry", inSrid, out _, out error))
        {
            return false;
        }

        var where = GetString(values, "where");
        if (where is { Length: > 2000 })
        {
            error = "where clause exceeds the maximum length of 2000 characters.";
            return false;
        }

        if (!TryParseObjectIds(GetString(values, "objectIds"), out var objectIds, out error))
        {
            return false;
        }

        if (!TryParseMaxCount(GetString(values, "maxCount"), out maxCount, out error))
        {
            return false;
        }

        query = new ImageServerCatalogQuery
        {
            Where = where,
            ObjectIds = objectIds,
            SpatialFilter = new ImageServerCatalogSpatialFilter(
                target.X,
                target.Y,
                target.X,
                target.Y,
                target.Srid),
            Limit = MaxFindCount,
            ReturnGeometry = false,
        };

        return true;
    }

    private static bool TryParsePoint(
        string? raw,
        string parameterName,
        int? explicitSrid,
        out FindPoint point,
        out string? error)
    {
        point = default;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = $"{parameterName} parameter is required.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetJsonDouble(root, "x", out var x) ||
                !TryGetJsonDouble(root, "y", out var y))
            {
                error = $"{parameterName} must be an Esri point geometry with x and y.";
                return false;
            }

            double? z = null;
            if (root.TryGetProperty("z", out var zElement))
            {
                if (zElement.ValueKind != JsonValueKind.Number || !zElement.TryGetDouble(out var parsedZ))
                {
                    error = $"{parameterName} z coordinate must be numeric.";
                    return false;
                }

                z = parsedZ;
            }

            var srid = explicitSrid ?? ReadWkid(root);
            point = new FindPoint(x, y, z, srid);
            return true;
        }
        catch (JsonException)
        {
            error = $"{parameterName} must be valid JSON.";
            return false;
        }
    }

    private static bool TryParseMaxCount(string? raw, out int maxCount, out string? error)
    {
        maxCount = DefaultMaxCount;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ||
            parsed <= 0)
        {
            error = "maxCount must be a positive integer.";
            return false;
        }

        maxCount = Math.Min(parsed, MaxFindCount);
        return true;
    }

    private static bool TryParseObjectIds(string? raw, out IReadOnlyList<long>? objectIds, out string? error)
    {
        objectIds = null;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var trimmed = raw.Trim();
        var ids = new List<long>();
        if (trimmed.StartsWith('['))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    error = "objectIds must be a comma-separated list or JSON array.";
                    return false;
                }

                foreach (var element in document.RootElement.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var numericId))
                    {
                        ids.Add(numericId);
                    }
                    else if (element.ValueKind == JsonValueKind.String &&
                             long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stringId))
                    {
                        ids.Add(stringId);
                    }
                    else
                    {
                        error = "objectIds must contain integer values.";
                        return false;
                    }
                }
            }
            catch (JsonException)
            {
                error = "objectIds JSON array is invalid.";
                return false;
            }
        }
        else
        {
            foreach (var part in trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                {
                    error = "objectIds must contain integer values.";
                    return false;
                }

                ids.Add(id);
            }
        }

        objectIds = ids.Count == 0 ? null : ids;
        return true;
    }

    private static ImageServerFindImage BuildImage(ImageServerCatalogItem item)
    {
        var pixelSize = item.MinPixelSize > 0 ? item.MinPixelSize :
            item.MaxPixelSize > 0 ? item.MaxPixelSize : (double?)null;

        return new ImageServerFindImage
        {
            Id = item.ObjectId,
            Uri = item.Name,
            AcquisitionDate = item.AcquisitionDate?.ToUnixTimeMilliseconds(),
            Center = new ImageServerFindPoint
            {
                X = item.CenterX,
                Y = item.CenterY,
                SpatialReference = item.FootprintSrid.HasValue
                    ? new SpatialReference { Wkid = item.FootprintSrid.Value, LatestWkid = item.FootprintSrid.Value }
                    : null,
            },
            PixelSize = pixelSize,
            Rows = item.Height,
            Cols = item.Width,
        };
    }

    /// <summary>
    /// Orientation sort key: the image's off-nadir angle (degrees from straight-down). Smaller is
    /// better (more nadir, less oblique distortion over the target). Images without an off-nadir
    /// angle sort last so metadata-bearing candidates are preferred.
    /// </summary>
    private static double OffNadirSortKey(ImageServerCatalogItem item)
        => ImageServerSensorModel.TryReadOffNadirAngle(item.SensorMetadata) ?? double.MaxValue;

    private static double DistanceSortKey(ImageServerCatalogItem item, FindPoint target)
    {
        if (target.Srid.HasValue && item.FootprintSrid.HasValue && target.Srid.Value != item.FootprintSrid.Value)
        {
            return 0d;
        }

        var dx = item.CenterX - target.X;
        var dy = item.CenterY - target.Y;
        return (dx * dx) + (dy * dy);
    }

    private static int? ReadWkid(JsonElement root)
    {
        if (!root.TryGetProperty("spatialReference", out var sr) || sr.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (sr.TryGetProperty("latestWkid", out var latest) &&
            latest.ValueKind == JsonValueKind.Number &&
            latest.TryGetInt32(out var latestWkid))
        {
            return latestWkid;
        }

        if (sr.TryGetProperty("wkid", out var wkid) &&
            wkid.ValueKind == JsonValueKind.Number &&
            wkid.TryGetInt32(out var wkidValue))
        {
            return wkidValue;
        }

        return null;
    }

    private static bool TryGetJsonDouble(JsonElement element, string propertyName, out double value)
    {
        value = 0d;
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetDouble(out value);
    }

    private static string? GetString(IReadOnlyDictionary<string, StringValues> values, string key)
        => values.TryGetValue(key, out var raw) ? raw.ToString() : null;
}

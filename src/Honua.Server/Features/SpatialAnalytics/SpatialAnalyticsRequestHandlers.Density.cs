// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.SpatialAnalytics.Abstractions;
using Honua.Core.Features.SpatialAnalytics.Domain;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer;
using Honua.Server.Features.Infrastructure.Analytics;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.SpatialAnalytics.Models;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.SpatialAnalytics;

/// <summary>
/// Density binning handler. REST entry:
/// <c>POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryDensity</c>.
/// OGC mirror: <c>POST /ogc/features/collections/{collectionId}/density</c>.
/// </summary>
internal static partial class SpatialAnalyticsRequestHandlers
{
    private const string DensityOperation = "density";

    public static async Task<IResult> HandleDensityPost(
        string serviceId,
        int layerId,
        HttpContext context)
    {
        var logger = context.RequestServices
            .GetService<ILoggerFactory>()?.CreateLogger("Honua.Server.Features.SpatialAnalytics.Density");
        var editionError = RequireProEdition(context, DensityOperation, logger);
        if (editionError != null)
        {
            return editionError;
        }

        var cancellationToken = FeatureServerEndpoints.GetTimeoutAwareCancellationToken(context);

        var (values, readError) = await ReadRequestValuesAsync(context, cancellationToken);
        if (values == null)
        {
            return readError!;
        }

        var (layer, _, resourceError) = await ValidateRestResourceAsync(
            context, serviceId, layerId, cancellationToken);
        if (resourceError != null)
        {
            return resourceError;
        }

        return await HandleDensityCoreAsync(
            context,
            values,
            layer!,
            serviceId,
            HonuaTelemetry.Protocols.FeatureServer,
            logger,
            cancellationToken);
    }

    public static async Task<IResult> HandleOgcDensityPost(
        string collectionId,
        HttpContext context)
    {
        var logger = context.RequestServices
            .GetService<ILoggerFactory>()?.CreateLogger("Honua.Server.Features.SpatialAnalytics.Density");
        var editionError = RequireProEdition(context, DensityOperation, logger);
        if (editionError != null)
        {
            return editionError;
        }

        var cancellationToken = FeatureServerEndpoints.GetTimeoutAwareCancellationToken(context);

        var (values, readError) = await ReadRequestValuesAsync(context, cancellationToken);
        if (values == null)
        {
            return readError!;
        }

        var (layer, resourceError) = await ValidateOgcResourceAsync(context, collectionId, cancellationToken);
        if (resourceError != null)
        {
            return resourceError;
        }

        return await HandleDensityCoreAsync(
            context,
            values,
            layer!,
            serviceId: null,
            HonuaTelemetry.Protocols.OgcFeatures,
            logger,
            cancellationToken);
    }

    private static async Task<IResult> HandleDensityCoreAsync(
        HttpContext context,
        IReadOnlyDictionary<string, StringValues> values,
        LayerDefinition layer,
        string? serviceId,
        string protocol,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        using var activity = StartAnalyticsActivity(DensityOperation, protocol, serviceId, layer.Id);

        if (logger != null)
        {
            SpatialAnalyticsLog.RequestReceived(logger, DensityOperation, layer.Id);
        }

        var startTimestamp = TimeProvider.System.GetTimestamp();
        var analyticsLimits = context.RequestServices
            .GetRequiredService<IOptions<LimitsOptions>>().Value.Analytics;

        // mode (default: hex grid).
        var modeStr = GetValueString(values, SpatialAnalyticsParameters.Mode);
        DensityBinningMode mode;
        if (string.IsNullOrWhiteSpace(modeStr))
        {
            mode = DensityBinningMode.HexGrid;
        }
        else
        {
            switch (modeStr.ToLowerInvariant())
            {
                case "hex":
                case "hexgrid":
                case "hex-grid":
                    mode = DensityBinningMode.HexGrid;
                    break;
                case "square":
                case "squaregrid":
                case "square-grid":
                    mode = DensityBinningMode.SquareGrid;
                    break;
                default:
                    return StandardErrorHelpers.CreateBadRequest(context,
                        "Invalid mode",
                        ["mode must be either 'hex' or 'square'."]);
            }
        }

        activity?.SetTag("density.mode", mode.ToString());

        // cellSize (required, in meters).
        var cellSizeStr = GetValueString(values, SpatialAnalyticsParameters.CellSize);
        if (string.IsNullOrWhiteSpace(cellSizeStr) ||
            !double.TryParse(cellSizeStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var cellSize) ||
            double.IsNaN(cellSize) || double.IsInfinity(cellSize))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "cellSize parameter is required",
                ["cellSize must be a number (meters)."]);
        }

        if (cellSize < analyticsLimits.MinDensityCellSizeMeters ||
            cellSize > analyticsLimits.MaxDensityCellSizeMeters)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid cellSize",
                [$"cellSize must be between {analyticsLimits.MinDensityCellSizeMeters} and {analyticsLimits.MaxDensityCellSizeMeters} meters."]);
        }

        activity?.SetTag("density.cellSize", cellSize);

        // weightField (optional).
        var weightField = GetValueString(values, SpatialAnalyticsParameters.WeightField);
        if (string.IsNullOrWhiteSpace(weightField))
        {
            weightField = null;
        }

        // Feature query (where + SQL filter + objectIds + spatial + temporal).
        var (featureQuery, filterError) = await AnalyticsFeatureQueryFactory.TryBuildAsync(
            context, values, layer, cancellationToken);
        if (featureQuery == null)
        {
            return filterError!;
        }

        var densityQuery = new DensityQuery
        {
            Mode = mode,
            CellSizeMeters = cellSize,
            WeightField = weightField,
            MaxCells = analyticsLimits.MaxDensityCells,
            MaxInputFeatures = analyticsLimits.MaxInputFeatures
        };

        if (!TryGetAnalyticsReader(context, DensityOperation, logger, out var reader, out var readerError))
        {
            return readerError!;
        }

        ImmutableArray<IReadOnlyDictionary<string, object?>> rows;
        try
        {
            rows = await reader.QueryDensityAsync(layer.Id, featureQuery.Value, densityQuery, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (logger != null)
            {
                SpatialAnalyticsLog.RequestFailed(logger, DensityOperation, layer.Id, ex.Message, ex);
            }
            return CreateReaderFailureResult(context, DensityOperation, ex);
        }

        // Density binning applies both caps: MaxInputFeatures on the src CTE and
        // MaxDensityCells on the outer SELECT. The cell-level COUNT(*) does not
        // reflect the pre-cap input volume, so the SQL builder emits "_inputCount"
        // on every row and the handler reads it once before stripping it.
        var inputCount = ExtractInputCount(ref rows);
        var (inputTruncated, resultTruncated) = DetectOverflow(
            inputCount, rows.Length, analyticsLimits.MaxInputFeatures, analyticsLimits.MaxDensityCells);
        if (inputTruncated && logger != null)
        {
            SpatialAnalyticsLog.InputTruncated(logger, DensityOperation, layer.Id, analyticsLimits.MaxInputFeatures);
        }
        if (resultTruncated && logger != null)
        {
            SpatialAnalyticsLog.ResultTruncated(logger, DensityOperation, layer.Id, analyticsLimits.MaxDensityCells);
        }

        rows = TrimOverflowRows(rows, analyticsLimits.MaxInputFeatures, analyticsLimits.MaxDensityCells);

        var features = new SpatialAnalyticsFeature[rows.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            features[i] = MapRowToFeature(rows[i]);
        }

        var elapsed = TimeProvider.System.GetElapsedTime(startTimestamp);
        if (logger != null)
        {
            SpatialAnalyticsLog.RequestCompleted(
                logger, DensityOperation, layer.Id, features.Length, elapsed.TotalMilliseconds);
        }

        var response = new SpatialAnalyticsFeatureCollection
        {
            Features = features,
            NumberReturned = features.Length,
            Metadata = new SpatialAnalyticsMetadata
            {
                Operation = DensityOperation,
                InputTruncated = inputTruncated,
                ResultTruncated = resultTruncated,
                MaxInputFeatures = analyticsLimits.MaxInputFeatures,
                MaxOutputRows = analyticsLimits.MaxDensityCells
            }
        };

        return WriteAnalyticsResponse(response);
    }
}

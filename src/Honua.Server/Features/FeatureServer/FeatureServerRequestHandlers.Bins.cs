// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    private static async Task<IResult> HandleQueryBinsGet(
        string serviceId,
        int layerId,
        HttpContext context)
    {
        var values = ToCaseInsensitiveDictionary(context.Request.Query);
        return await HandleQueryBinsCore(serviceId, layerId, values, context);
    }

    private static async Task<IResult> HandleQueryBinsPost(
        string serviceId,
        int layerId,
        HttpContext context)
    {
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var (values, readError) = await TryReadRequestValuesAsync(context.Request, cancellationToken);
        if (values == null)
        {
            if (TryGetUnsupportedMediaType(readError, out var receivedContentType))
            {
                return CreateUnsupportedRequestContentTypeResult(receivedContentType);
            }

            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid request body",
                [readError ?? "Invalid request body."]);
        }

        return await HandleQueryBinsCore(serviceId, layerId, values, context);
    }

    private static async Task<IResult> HandleQueryBinsCore(
        string serviceId,
        int layerId,
        IReadOnlyDictionary<string, StringValues> values,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("featureserver.queryBins");
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var validationResult = await FeatureServerResourceValidationHelpers.ValidateServiceLayerAsync(
            resourceValidator,
            serviceId,
            layerId,
            context,
            logger: null,
            cancellationToken: cancellationToken);
        if (!validationResult.IsValid)
        {
            return validationResult.ErrorResult!;
        }

        var service = validationResult.Service!;
        var layer = validationResult.Layer!;
        var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer, service);
        if (accessError != null)
        {
            return accessError;
        }

        // Parse bin JSON parameter
        var binJson = GetValueString(values, "bin");
        if (string.IsNullOrWhiteSpace(binJson))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "bin parameter is required");
        }

        if (!TryParseBinDefinition(binJson, out var binDefinition, out var binParseError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid bin parameter",
                [binParseError ?? "bin must be valid JSON."]);
        }

        // Parse optional outStatistics
        var outStatsJson = GetValueString(values, "outStatistics");
        if (!string.IsNullOrWhiteSpace(outStatsJson))
        {
            if (!TryParseOutStatistics(outStatsJson, out var outStats, out var statsError))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid outStatistics parameter",
                    [statsError ?? "outStatistics must be valid JSON."]);
            }

            binDefinition = binDefinition with { OutStatistics = outStats };
        }

        var query = new FeatureQuery
        {
            Where = GetValueString(values, "where"),
            SpatialReferenceSrid = layer.SpatialReference.ToSrid()
        };

        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var rows = await featureReader.QueryBinsAsync(layer.Id, query, binDefinition, cancellationToken);

        var responseFeatures = rows.Select(row => new GeoServicesFeature
        {
            Attributes = new Dictionary<string, object?>(row)
        }).ToArray();

        var response = new QueryResponse
        {
            Features = responseFeatures
        };

        return Results.Json(response, FeatureServerJsonContext.Default.QueryResponse, contentType: "application/json");
    }

    private static bool TryParseBinDefinition(string json, out BinDefinition binDefinition, out string? error)
    {
        binDefinition = default;
        error = null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "bin must be a JSON object.";
                return false;
            }

            // fixedIntervalBin
            if (root.TryGetProperty("fixedIntervalBin", out var fixedInterval) &&
                fixedInterval.ValueKind == JsonValueKind.Object)
            {
                return TryParseFixedIntervalBin(fixedInterval, out binDefinition, out error);
            }

            // autoIntervalBin
            if (root.TryGetProperty("autoIntervalBin", out var autoInterval) &&
                autoInterval.ValueKind == JsonValueKind.Object)
            {
                return TryParseAutoIntervalBin(autoInterval, out binDefinition, out error);
            }

            // fixedBoundariesBin
            if (root.TryGetProperty("fixedBoundariesBin", out var fixedBoundaries) &&
                fixedBoundaries.ValueKind == JsonValueKind.Object)
            {
                return TryParseFixedBoundariesBin(fixedBoundaries, out binDefinition, out error);
            }

            // dateBin
            if (root.TryGetProperty("dateBin", out var dateBin) &&
                dateBin.ValueKind == JsonValueKind.Object)
            {
                return TryParseDateBinForQueryBins(dateBin, out binDefinition, out error);
            }

            // classificationBin
            if (root.TryGetProperty("classificationBin", out var classBin) &&
                classBin.ValueKind == JsonValueKind.Object)
            {
                return TryParseClassificationBin(classBin, out binDefinition, out error);
            }

            error = "bin must contain one of: fixedIntervalBin, autoIntervalBin, fixedBoundariesBin, dateBin, classificationBin.";
            return false;
        }
        catch (JsonException)
        {
            error = "bin is not valid JSON.";
            return false;
        }
    }

    private static bool TryParseFixedIntervalBin(JsonElement element, out BinDefinition binDef, out string? error)
    {
        binDef = default;
        error = null;

        if (!element.TryGetProperty("field", out var fieldEl) || string.IsNullOrWhiteSpace(fieldEl.GetString()))
        {
            error = "fixedIntervalBin.field is required.";
            return false;
        }

        if (!element.TryGetProperty("intervalSize", out var sizeEl) || !sizeEl.TryGetDouble(out var intervalSize))
        {
            error = "fixedIntervalBin.intervalSize is required.";
            return false;
        }

        double? start = null, end = null;
        if (element.TryGetProperty("start", out var startEl) && startEl.TryGetDouble(out var startVal))
        {
            start = startVal;
        }

        if (element.TryGetProperty("end", out var endEl) && endEl.TryGetDouble(out var endVal))
        {
            end = endVal;
        }

        binDef = new BinDefinition
        {
            Type = BinType.FixedInterval,
            Field = fieldEl.GetString()!,
            IntervalSize = intervalSize,
            IntervalStart = start,
            IntervalEnd = end
        };
        return true;
    }

    private static bool TryParseAutoIntervalBin(JsonElement element, out BinDefinition binDef, out string? error)
    {
        binDef = default;
        error = null;

        if (!element.TryGetProperty("field", out var fieldEl) || string.IsNullOrWhiteSpace(fieldEl.GetString()))
        {
            error = "autoIntervalBin.field is required.";
            return false;
        }

        if (!element.TryGetProperty("numBins", out var numBinsEl) || !numBinsEl.TryGetInt32(out var numBins))
        {
            error = "autoIntervalBin.numBins is required.";
            return false;
        }

        if (numBins <= 0)
        {
            error = "autoIntervalBin.numBins must be a positive integer.";
            return false;
        }

        binDef = new BinDefinition
        {
            Type = BinType.AutoInterval,
            Field = fieldEl.GetString()!,
            NumBins = numBins
        };
        return true;
    }

    private static bool TryParseFixedBoundariesBin(JsonElement element, out BinDefinition binDef, out string? error)
    {
        binDef = default;
        error = null;

        if (!element.TryGetProperty("field", out var fieldEl) || string.IsNullOrWhiteSpace(fieldEl.GetString()))
        {
            error = "fixedBoundariesBin.field is required.";
            return false;
        }

        if (!element.TryGetProperty("boundaries", out var boundariesEl) || boundariesEl.ValueKind != JsonValueKind.Array)
        {
            error = "fixedBoundariesBin.boundaries is required and must be an array.";
            return false;
        }

        var boundaries = ImmutableArray.CreateBuilder<double>();
        foreach (var item in boundariesEl.EnumerateArray())
        {
            if (!item.TryGetDouble(out var val))
            {
                error = "fixedBoundariesBin.boundaries must contain numeric values.";
                return false;
            }

            boundaries.Add(val);
        }

        if (boundaries.Count < 2)
        {
            error = "fixedBoundariesBin.boundaries must contain at least 2 values.";
            return false;
        }

        binDef = new BinDefinition
        {
            Type = BinType.FixedBoundaries,
            Field = fieldEl.GetString()!,
            Boundaries = boundaries.ToImmutable()
        };
        return true;
    }

    private static bool TryParseDateBinForQueryBins(JsonElement element, out BinDefinition binDef, out string? error)
    {
        binDef = default;
        error = null;

        if (!element.TryGetProperty("field", out var fieldEl) || string.IsNullOrWhiteSpace(fieldEl.GetString()))
        {
            error = "dateBin.field is required.";
            return false;
        }

        var field = fieldEl.GetString()!;

        // Parse inner bin definition (calendarBin or fixedBin)
        string? calendarUnit = null;
        TimeSpan? fixedInterval = null;

        if (element.TryGetProperty("calendarBin", out var calEl) && calEl.ValueKind == JsonValueKind.Object)
        {
            calendarUnit = calEl.TryGetProperty("unit", out var unitEl) ? unitEl.GetString()?.ToLowerInvariant() : null;
            if (string.IsNullOrWhiteSpace(calendarUnit))
            {
                error = "dateBin.calendarBin.unit is required.";
                return false;
            }
        }
        else if (element.TryGetProperty("fixedBin", out var fixEl) && fixEl.ValueKind == JsonValueKind.Object)
        {
            if (!fixEl.TryGetProperty("intervalCount", out var countEl) || !countEl.TryGetInt32(out var count))
            {
                error = "dateBin.fixedBin.intervalCount is required.";
                return false;
            }

            var unit = fixEl.TryGetProperty("intervalUnit", out var unitEl)
                ? unitEl.GetString()?.ToLowerInvariant() ?? "days"
                : "days";
            fixedInterval = ConvertToTimeSpan(count, unit);
        }
        else
        {
            error = "dateBin must contain calendarBin or fixedBin.";
            return false;
        }

        binDef = new BinDefinition
        {
            Type = BinType.Date,
            Field = field,
            DateBin = new DateBinDefinition
            {
                BinField = field,
                CalendarUnit = calendarUnit,
                FixedInterval = fixedInterval
            }
        };
        return true;
    }

    private static bool TryParseClassificationBin(JsonElement element, out BinDefinition binDef, out string? error)
    {
        binDef = default;
        error = null;

        if (!element.TryGetProperty("field", out var fieldEl) || string.IsNullOrWhiteSpace(fieldEl.GetString()))
        {
            error = "classificationBin.field is required.";
            return false;
        }

        binDef = new BinDefinition
        {
            Type = BinType.Classification,
            Field = fieldEl.GetString()!
        };
        return true;
    }
}

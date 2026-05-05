// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    private static async Task<IResult> HandleQueryDateBinsGet(
        string serviceId,
        int layerId,
        HttpContext context)
    {
        var values = ToCaseInsensitiveDictionary(context.Request.Query);
        return await HandleQueryDateBinsCore(serviceId, layerId, values, context);
    }

    private static async Task<IResult> HandleQueryDateBinsPost(
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
                return CreateUnsupportedRequestContentTypeResult(context, receivedContentType);
            }

            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid request body",
                [readError ?? "Invalid request body."]);
        }

        return await HandleQueryDateBinsCore(serviceId, layerId, values, context);
    }

    private static async Task<IResult> HandleQueryDateBinsCore(
        string serviceId,
        int layerId,
        IReadOnlyDictionary<string, StringValues> values,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("featureserver.queryDateBins");
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

        // queryDateBins is the temporal histogram surface (feature key
        // `temporal.histogram` in FeatureCatalog) and is documented as Pro in
        // docs/gis/temporal-animation-api.md. Unlike the MVT temporal tile
        // path there is no documented no-op input that would render the gate
        // a no-op, so the gate fires for any successful access here.
        var editionError = RequireProEditionForDateBins(context);
        if (editionError != null)
        {
            return editionError;
        }

        // Parse required binField
        var binField = GetValueString(values, "binField");
        if (string.IsNullOrWhiteSpace(binField))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "binField parameter is required");
        }

        // Parse bin JSON parameter
        var binJson = GetValueString(values, "bin");
        if (string.IsNullOrWhiteSpace(binJson))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "bin parameter is required");
        }

        if (!TryParseDateBin(binJson, binField, out var dateBin, out var binParseError))
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

            dateBin = dateBin with { OutStatistics = outStats };
        }

        var query = new FeatureQuery
        {
            Where = GetValueString(values, "where"),
            SpatialReferenceSrid = layer.SpatialReference.ToSrid()
        };

        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var rows = await featureReader.QueryDateBinsAsync(layer.Id, query, dateBin, cancellationToken);

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

    /// <summary>
    /// Pro-edition gate for the queryDateBins temporal histogram. Mirrors
    /// <c>RequireProEditionForTimeSeriesTiles</c> in FeatureServerRequestHandlers.Tiles.cs:
    /// resolves the active license, returns an HTTP 403 with a remediation
    /// message on Community, and lets Pro / Enterprise through. Exposed as
    /// <c>internal</c> so the gate decision can be unit-tested directly without
    /// booting the full HTTP pipeline (mirrors the
    /// <c>SpatialAnalyticsRequestHandlers.RequireProEdition</c> shape).
    /// </summary>
    internal static IResult? RequireProEditionForDateBins(HttpContext context)
    {
        var licenseProvider = context.RequestServices.GetRequiredService<ILicenseStatusProvider>();
        var edition = licenseProvider.GetCurrentStatus().Edition;
        if (edition >= HonuaEdition.Pro)
        {
            return null;
        }

        return StandardErrorHelpers.CreateForbidden(
            context,
            $"Temporal histogram (queryDateBins) requires the Pro edition or higher. Current edition: {edition}.");
    }

    private static bool TryParseDateBin(string json, string binField, out DateBinDefinition dateBin, out string? error)
    {
        dateBin = default;
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

            // Check for calendarBin
            if (root.TryGetProperty("calendarBin", out var calendarElement) &&
                calendarElement.ValueKind == JsonValueKind.Object)
            {
                if (!calendarElement.TryGetProperty("unit", out var unitElement))
                {
                    error = "calendarBin.unit is required.";
                    return false;
                }

                var unit = unitElement.GetString();
                if (string.IsNullOrWhiteSpace(unit))
                {
                    error = "calendarBin.unit must not be empty.";
                    return false;
                }

                dateBin = new DateBinDefinition
                {
                    BinField = binField,
                    CalendarUnit = unit.ToLowerInvariant()
                };
                return true;
            }

            // Check for fixedBin
            if (root.TryGetProperty("fixedBin", out var fixedElement) &&
                fixedElement.ValueKind == JsonValueKind.Object)
            {
                if (!fixedElement.TryGetProperty("intervalCount", out var countElement) ||
                    !countElement.TryGetInt32(out var intervalCount))
                {
                    error = "fixedBin.intervalCount is required.";
                    return false;
                }

                if (!fixedElement.TryGetProperty("intervalUnit", out var intervalUnitElement))
                {
                    error = "fixedBin.intervalUnit is required.";
                    return false;
                }

                var intervalUnit = intervalUnitElement.GetString()?.ToLowerInvariant() ?? "days";
                var interval = ConvertToTimeSpan(intervalCount, intervalUnit);

                DateTimeOffset? origin = null;
                if (fixedElement.TryGetProperty("origin", out var originElement) &&
                    originElement.ValueKind == JsonValueKind.Number &&
                    originElement.TryGetInt64(out var originMs))
                {
                    origin = DateTimeOffset.FromUnixTimeMilliseconds(originMs);
                }

                dateBin = new DateBinDefinition
                {
                    BinField = binField,
                    FixedInterval = interval,
                    FixedIntervalOrigin = origin
                };
                return true;
            }

            error = "bin must contain either calendarBin or fixedBin.";
            return false;
        }
        catch (JsonException)
        {
            error = "bin is not valid JSON.";
            return false;
        }
    }

    private static TimeSpan ConvertToTimeSpan(int count, string unit)
    {
        return unit switch
        {
            "seconds" => TimeSpan.FromSeconds(count),
            "minutes" => TimeSpan.FromMinutes(count),
            "hours" => TimeSpan.FromHours(count),
            "days" => TimeSpan.FromDays(count),
            "weeks" => TimeSpan.FromDays(count * 7),
            _ => TimeSpan.FromDays(count)
        };
    }

    private static bool TryParseOutStatistics(string json, out ImmutableArray<StatisticDefinition> outStats, out string? error)
    {
        outStats = ImmutableArray<StatisticDefinition>.Empty;
        error = null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
            {
                error = "outStatistics must be a JSON array.";
                return false;
            }

            var builder = ImmutableArray.CreateBuilder<StatisticDefinition>();
            foreach (var element in root.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    error = "Each outStatistics entry must be a JSON object.";
                    return false;
                }

                var statisticType = element.TryGetProperty("statisticType", out var typeElement)
                    ? typeElement.GetString()
                    : null;
                var onField = element.TryGetProperty("onStatisticField", out var fieldElement)
                    ? fieldElement.GetString()
                    : null;
                var outFieldName = element.TryGetProperty("outStatisticFieldName", out var outElement)
                    ? outElement.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(statisticType) ||
                    string.IsNullOrWhiteSpace(onField) ||
                    string.IsNullOrWhiteSpace(outFieldName))
                {
                    error = "Each outStatistics entry requires statisticType, onStatisticField, and outStatisticFieldName.";
                    return false;
                }

                if (!TryParseStatisticType(statisticType, out var parsedType))
                {
                    error = $"Unsupported statisticType: {statisticType}.";
                    return false;
                }

                builder.Add(new StatisticDefinition
                {
                    StatisticType = parsedType,
                    OnStatisticField = onField,
                    OutStatisticFieldName = outFieldName
                });
            }

            outStats = builder.ToImmutable();
            return true;
        }
        catch (JsonException)
        {
            error = "outStatistics is not valid JSON.";
            return false;
        }
    }

    private static bool TryParseStatisticType(string type, out StatisticType result)
    {
        result = type.ToLowerInvariant() switch
        {
            "count" => StatisticType.Count,
            "sum" => StatisticType.Sum,
            "min" => StatisticType.Min,
            "max" => StatisticType.Max,
            "avg" => StatisticType.Avg,
            "stddev" => StatisticType.Stddev,
            "var" => StatisticType.Var,
            _ => default
        };

        return type.ToLowerInvariant() is "count" or "sum" or "min" or "max" or "avg" or "stddev" or "var";
    }
}

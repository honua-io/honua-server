// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Licensing;
using Honua.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    private static async Task<IResult> HandleQueryDateBinsGet(
        string serviceId,
        int layerId,
        HttpContext context)
    {
        var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
        if (!TryValidateAllowedParameters(context.Request.Query, queryValidator, AllowedQueryParameters.QueryDateBins, out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

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

        var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
        if (!TryValidateAllowedParameters(values, queryValidator, AllowedQueryParameters.QueryDateBins, out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
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
        var validationResult = await FeatureServerResourceValidationHelpers.ValidateServiceLayerV2Async(
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
        var publication = validationResult.Publication!;
        var resource = validationResult.Resource!;
        var accessError = await AccessPolicyHelpers.RequireResourceAccessAsync(
            context, resource, AuthorizationOperation.Query, service, cancellationToken).ConfigureAwait(false);
        if (accessError != null)
        {
            return accessError;
        }

        var snapshotProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await snapshotProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var storageLayerId = ResolveFeatureServerStorageLayerIdV2(snapshot, publication, resource);
        if (storageLayerId is null)
        {
            return StandardErrorHelpers.CreateNotFound(context,
                $"Layer '{resource.Metadata.Name ?? layerId.ToString(CultureInfo.InvariantCulture)}' is not bound to a storage layer.");
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

        var requestedFormat = GetValueString(values, "f");
        if (!TryValidateOutputFormat(requestedFormat, JsonOnlyFormats, out _, out var formatError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [formatError ?? "Output format is not supported."]);
        }

        // Parse required binField
        var binField = GetValueString(values, "binField");
        if (string.IsNullOrWhiteSpace(binField))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "binField parameter is required");
        }

        if (!IsSchemaField(resource, binField))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid binField parameter",
                [$"Field '{binField}' does not exist on the layer."]);
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

            if (!TryValidateStatisticsFields(resource, outStats, out var statsFieldError))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid outStatistics parameter",
                    [statsFieldError ?? "outStatistics references an unknown field."]);
            }

            dateBin = dateBin with { OutStatistics = outStats };
        }

        // Translate the where clause through the shared filter pipeline (matching
        // /query and queryH3) so valid ArcGIS SQL is supported and invalid input
        // surfaces as a protocol-shaped 400 instead of a provider exception.
        if (!TryBuildAnalyticsFeatureQuery(context, resource, GetValueString(values, "where"), out var query, out var whereError))
        {
            return whereError!;
        }

        var (filteredQuery, filterError) = await ApplyAnalyticsFiltersAsync(
            context, resource, values, query, cancellationToken).ConfigureAwait(false);
        if (filterError != null)
        {
            return filterError;
        }

        query = filteredQuery;

        var featureReader = await ResolveAnalyticsFeatureReaderAsync(
            context, snapshot, service, resource, publication, storageLayerId.Value, cancellationToken).ConfigureAwait(false);
        ImmutableArray<IReadOnlyDictionary<string, object?>> rows;
        try
        {
            rows = await featureReader.QueryDateBinsAsync(storageLayerId.Value, query, dateBin, cancellationToken);
        }
        catch (NotSupportedException)
        {
            return StandardErrorHelpers.CreateNotImplemented(
                context,
                "queryDateBins is not supported by the configured feature provider for this layer.");
        }

        var responseFeatures = rows.Select(row => new GeoServicesFeature
        {
            Attributes = new Dictionary<string, object?>(row)
        }).ToArray();

        var response = new QueryResponse
        {
            Fields = BuildDateBinFields(resource, dateBin.OutStatistics ?? ImmutableArray<StatisticDefinition>.Empty),
            Features = responseFeatures
        };

        return Results.Json(response, FeatureServerJsonContext.Default.QueryResponse, contentType: "application/json");
    }

    internal static GeoServicesFieldInfo[] BuildDateBinFields(
        MetadataV2Object resource,
        ImmutableArray<StatisticDefinition> outStatistics)
    {
        var fields = new List<GeoServicesFieldInfo>
        {
            new()
            {
                Name = "boundary",
                Type = "esriFieldTypeDate",
                Alias = "boundary",
                Editable = false,
                Nullable = true
            }
        };

        AppendBinStatisticsFields(fields, resource, outStatistics);
        return [.. fields];
    }

    internal static IResult? RequireProEditionForDateBins(HttpContext context)
    {
        return LicenseGate.RequireEntitlement(
            context,
            "temporal.histogram",
            "Temporal histogram");
    }

    internal static bool TryParseDateBin(string json, string binField, out DateBinDefinition dateBin, out string? error)
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
                var unit = FeatureServerJsonMembers.ReadString(calendarElement, "unit");
                if (string.IsNullOrWhiteSpace(unit))
                {
                    error = "calendarBin.unit is required and must be a non-empty string.";
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
                var (interval, intervalError) = ParseFixedBinInterval(fixedElement, "fixedBin", defaultUnit: null);
                if (intervalError is not null)
                {
                    error = intervalError;
                    return false;
                }

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
                    FixedInterval = interval!.Value,
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

    // A fixed bin has one length, so only fixed-length units are accepted; months and years
    // vary in length and belong to calendarBin. Unknown units fail instead of binning by days.
    // Sub-second units are refused because the DuckDB builder bins in whole seconds.
    private static (TimeSpan? Interval, string? Error) ParseFixedBinInterval(
        JsonElement fixedElement,
        string paramName,
        string? defaultUnit)
    {
        if (FeatureServerJsonMembers.ReadInt32(fixedElement, "intervalCount") is not { } count)
        {
            return (null, $"{paramName}.intervalCount is required.");
        }

        if (count <= 0)
        {
            return (null, $"{paramName}.intervalCount must be a positive integer.");
        }

        if (!fixedElement.TryGetProperty("intervalUnit", out var unitElement))
        {
            return defaultUnit is null
                ? (null, $"{paramName}.intervalUnit is required.")
                : ResolveFixedInterval(count, defaultUnit, paramName);
        }

        var unit = unitElement.ValueKind == JsonValueKind.String
            ? unitElement.GetString()!.ToLowerInvariant()
            : string.Empty;
        return ResolveFixedInterval(count, unit, paramName);
    }

    private static (TimeSpan? Interval, string? Error) ResolveFixedInterval(int count, string unit, string paramName)
    {
        long? secondsPerUnit = unit switch
        {
            "seconds" => 1L,
            "minutes" => 60L,
            "hours" => 3600L,
            "days" => 86400L,
            "weeks" => 604800L,
            _ => null
        };
        if (secondsPerUnit is null)
        {
            return (null, unit is "months" or "years"
                ? $"{paramName}.intervalUnit '{unit}' has no fixed length; use calendarBin for months and years."
                : $"{paramName}.intervalUnit must be one of seconds, minutes, hours, days or weeks.");
        }

        var maxTimeSpanSeconds = TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerSecond;
        var totalSeconds = count * secondsPerUnit.Value;
        if (totalSeconds > maxTimeSpanSeconds)
        {
            return (null, $"{paramName}.intervalCount is too large.");
        }

        return (TimeSpan.FromSeconds(totalSeconds), null);
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

                var statisticType = FeatureServerJsonMembers.ReadString(element, "statisticType");
                var onField = FeatureServerJsonMembers.ReadString(element, "onStatisticField");
                var outFieldName = FeatureServerJsonMembers.ReadString(element, "outStatisticFieldName");

                if (string.IsNullOrWhiteSpace(statisticType) ||
                    string.IsNullOrWhiteSpace(onField) ||
                    string.IsNullOrWhiteSpace(outFieldName))
                {
                    error = "Each outStatistics entry requires statisticType, onStatisticField, and outStatisticFieldName.";
                    return false;
                }

                if (!StatisticTypeParser.TryParse(statisticType, out var parsedType))
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
}

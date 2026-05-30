// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    private const int DefaultCategoryLimit = 10;
    private const int MaxCategoryLimit = 100;
    private const int DefaultHistogramBins = 10;
    private const int MaxHistogramBins = 64;

    [GeneratedRegex(@"@p(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex SqlParameterRegex();

    private sealed record ParsedGroupBy(string Field, string? Alias, string? Label, int? Limit, string? NullLabel);

    private sealed record SpatialAggregationRequestPlan(
        string SourceId,
        string? RequestId,
        ImmutableArray<SpatialAggregationSummaryDefinition> Summaries,
        ImmutableArray<ParsedGroupBy> GroupBy,
        bool IncludeCells,
        bool IncludeTotals,
        int? PageLimit);

    private static bool HasSpatialAggregationSummaries(IReadOnlyDictionary<string, StringValues> values)
        => !string.IsNullOrWhiteSpace(GetValueString(values, "summaries"));

    private static async Task<(SpatialAggregationResultResponse? Response, IResult? Error)> HandleSpatialAggregationSummaryQueryAsync(
        HttpContext context,
        IReadOnlyDictionary<string, StringValues> values,
        string serviceId,
        int layerId,
        MetadataV2Resource resource,
        int storageLayerId,
        FeatureQuery featureQuery,
        int resolution,
        int? kRingDistance,
        int maxCells,
        CancellationToken cancellationToken)
    {
        if (!TryParseSpatialAggregationPlan(context, values, serviceId, layerId, resource, out var plan, out var parseError))
        {
            return (null, parseError);
        }

        var degraded = new List<SpatialAggregationDegradedReasonResponse>();
        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var summaryDefinitions = plan.Summaries;

        var totals = plan.IncludeTotals
            ? await BuildSpatialAggregationTotalsAsync(
                featureReader,
                resource,
                storageLayerId,
                featureQuery,
                summaryDefinitions,
                degraded,
                plan.SourceId,
                context,
                cancellationToken)
            : null;

        // Category cells use the authoritative total bucket list so every cell
        // reports the same stable buckets. Histogram cells need finite bounds
        // before the H3 query builder can emit bucket SQL.
        summaryDefinitions = ResolveCellSummaryDefinitions(summaryDefinitions, totals);

        var cellDefinitions = plan.IncludeCells
            ? await ResolveHistogramCellSummaryBoundsAsync(
                featureReader,
                storageLayerId,
                featureQuery,
                summaryDefinitions,
                degraded,
                plan.SourceId,
                cancellationToken)
            : summaryDefinitions;
        if (kRingDistance.GetValueOrDefault() > 0)
        {
            var supported = cellDefinitions
                .Where(summary => summary.Kind is SpatialAggregationSummaryKind.Count
                    or SpatialAggregationSummaryKind.Sum
                    or SpatialAggregationSummaryKind.Avg
                    or SpatialAggregationSummaryKind.Min
                    or SpatialAggregationSummaryKind.Max)
                .ToImmutableArray();

            if (supported.Length != cellDefinitions.Length)
            {
                degraded.Add(CreateDegraded(
                    plan.SourceId,
                    "Bucketed cell summaries are omitted for kRingDistance requests because neighbor expansion cannot re-aggregate category, histogram, or range buckets exactly."));
            }

            cellDefinitions = supported;
        }

        SpatialAggregationCellResponse[] cells = [];
        if (plan.IncludeCells)
        {
            var h3Query = new H3AggregationQuery
            {
                Resolution = resolution,
                KRingDistance = kRingDistance,
                SummaryDefinitions = cellDefinitions,
                MaxCells = plan.PageLimit.HasValue && plan.PageLimit.Value > 0
                    ? Math.Min(maxCells, plan.PageLimit.Value)
                    : maxCells
            };

            var rows = await featureReader.QueryH3Async(storageLayerId, featureQuery, h3Query, cancellationToken);
            cells = rows
                .Select(row => CreateSpatialAggregationCell(row, cellDefinitions, resolution))
                .ToArray();
        }

        var cellLimit = plan.PageLimit.HasValue && plan.PageLimit.Value > 0
            ? Math.Min(maxCells, plan.PageLimit.Value)
            : maxCells;
        var isPartial = plan.IncludeCells && cellLimit > 0 && cells.Length >= cellLimit;
        if (isPartial)
        {
            degraded.Add(CreateDegraded(
                plan.SourceId,
                "Returned cells may be partial because the H3 cell page reached the server transfer limit. Totals remain server-authoritative for the full filtered result."));
        }

        var indexModel = new SpatialAggregationIndexModelResponse
        {
            Id = SpatialAggregationContractConstants.H3IndexModelId,
            Title = "FeatureServer indexed cells",
            Family = SpatialAggregationContractConstants.H3IndexModelId,
            CellIdEncoding = "string",
            MinResolution = H3AggregationQuery.MinResolution,
            MaxResolution = H3AggregationQuery.MaxResolution,
            SupportedGeometry = ["none", "extent", "boundary"],
            Hierarchy = "parent-child",
            SpatialReference = new GeoServicesSpatialReference { Wkid = 4326 }
        };

        var metadata = BuildSpatialAggregationMetadata(plan, summaryDefinitions, indexModel, cells.Length, isPartial);

        var groups = plan.GroupBy.IsDefaultOrEmpty
            ? null
            : await BuildGroupedSummariesAsync(
                featureReader,
                storageLayerId,
                featureQuery,
                summaryDefinitions,
                plan.GroupBy,
                degraded,
                plan.SourceId,
                cancellationToken);

        var response = new SpatialAggregationResultResponse
        {
            RequestId = plan.RequestId,
            SourceId = plan.SourceId,
            GeneratedAt = DateTimeOffset.UtcNow,
            Index = new SpatialAggregationIndexStateResponse
            {
                Model = indexModel,
                Resolution = resolution,
                CellCount = cells.Length
            },
            Metadata = metadata,
            Cells = cells,
            Totals = totals,
            Groups = groups,
            Page = isPartial
                ? new SpatialAggregationPageInfoResponse { LoadedCellCount = cells.Length }
                : null,
            Degraded = degraded.Count == 0 ? null : degraded.ToArray()
        };

        return (response, null);
    }

    private static bool TryParseSpatialAggregationPlan(
        HttpContext context,
        IReadOnlyDictionary<string, StringValues> values,
        string serviceId,
        int layerId,
        MetadataV2Resource resource,
        out SpatialAggregationRequestPlan plan,
        out IResult? error)
    {
        plan = null!;
        error = null;

        if (!TryParseSpatialAggregationSummaries(context, values, resource, out var summaries, out error))
        {
            return false;
        }

        if (!TryParseSpatialAggregationGroupBy(context, values, resource, out var groupBy, out error))
        {
            return false;
        }

        if (!TryParseSpatialAggregationInclude(values, out var includeCells, out var includeTotals))
        {
            error = StandardErrorHelpers.CreateBadRequest(
                context,
                "Invalid include parameter",
                ["include must be a JSON object when supplied."]);
            return false;
        }

        if (!TryParsePageLimit(context, values, out var pageLimit, out error))
        {
            return false;
        }

        var sourceId = GetValueString(values, "sourceId");
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            sourceId = $"{SpatialAggregationContractConstants.FeatureServerProtocol}:{serviceId}/{layerId}";
        }

        plan = new SpatialAggregationRequestPlan(
            sourceId,
            GetValueString(values, "requestId"),
            summaries,
            groupBy,
            includeCells,
            includeTotals,
            pageLimit);
        return true;
    }

    private static bool TryParseSpatialAggregationSummaries(
        HttpContext context,
        IReadOnlyDictionary<string, StringValues> values,
        MetadataV2Resource resource,
        out ImmutableArray<SpatialAggregationSummaryDefinition> summaries,
        out IResult? error)
    {
        summaries = ImmutableArray<SpatialAggregationSummaryDefinition>.Empty;
        error = null;

        var raw = GetValueString(values, "summaries");
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = StandardErrorHelpers.CreateBadRequest(
                context,
                "summaries parameter is required",
                ["summaries must be a JSON array with at least one summary definition."]);
            return false;
        }

        if (!raw.TrimStart().StartsWith('['))
        {
            raw = $"[{raw}]";
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() == 0)
            {
                error = StandardErrorHelpers.CreateBadRequest(
                    context,
                    "Invalid summaries parameter",
                    ["summaries must be a non-empty JSON array."]);
                return false;
            }

            var builder = ImmutableArray.CreateBuilder<SpatialAggregationSummaryDefinition>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object ||
                    !TryReadStringProperty(element, "id", out var id) ||
                    !TryReadStringProperty(element, "kind", out var kindRaw) ||
                    string.IsNullOrWhiteSpace(id) ||
                    string.IsNullOrWhiteSpace(kindRaw))
                {
                    error = StandardErrorHelpers.CreateBadRequest(
                        context,
                        "Invalid summaries parameter",
                        ["Each summary requires string id and kind properties."]);
                    return false;
                }

                if (!ids.Add(id))
                {
                    error = StandardErrorHelpers.CreateBadRequest(
                        context,
                        "Invalid summaries parameter",
                        [$"Duplicate summary id '{id}'."]);
                    return false;
                }

                if (!TryParseSpatialAggregationSummaryKind(kindRaw, out var kind))
                {
                    error = StandardErrorHelpers.CreateBadRequest(
                        context,
                        "Invalid summaries parameter",
                        [$"Unsupported summary kind '{kindRaw}'."]);
                    return false;
                }

                if (!TryReadStringProperty(element, "field", out var field) ||
                    !TryReadStringProperty(element, "title", out var title) ||
                    !TryReadStringProperty(element, "unit", out var unit) ||
                    !TryReadStringProperty(element, "orderBy", out var orderBy))
                {
                    error = StandardErrorHelpers.CreateBadRequest(
                        context,
                        "Invalid summaries parameter",
                        ["Summary string properties must be JSON strings when supplied."]);
                    return false;
                }

                if (RequiresField(kind) && string.IsNullOrWhiteSpace(field))
                {
                    error = StandardErrorHelpers.CreateBadRequest(
                        context,
                        "Invalid summaries parameter",
                        [$"{kindRaw} summaries require a field."]);
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(field) && !IsSafeSpatialAggregationFieldName(field))
                {
                    error = StandardErrorHelpers.CreateBadRequest(
                        context,
                        "Invalid summaries parameter",
                        [$"Invalid summary field '{field}'."]);
                    return false;
                }

                if (!TryReadPositiveInt(element, "limit", out var limit) ||
                    !TryReadPositiveInt(element, "bins", out var bins) ||
                    !TryReadOptionalDouble(element, "min", out var min) ||
                    !TryReadOptionalDouble(element, "max", out var max))
                {
                    error = StandardErrorHelpers.CreateBadRequest(
                        context,
                        "Invalid summaries parameter",
                        ["Summary numeric properties must be finite positive values where required."]);
                    return false;
                }

                var includeOther = TryReadBool(element, "includeOther", out var parsedIncludeOther) && parsedIncludeOther;

                ImmutableArray<SpatialAggregationRangeBucketDefinition>? ranges = null;
                if (kind == SpatialAggregationSummaryKind.Range)
                {
                    if (!TryParseRangeBuckets(context, element, out ranges, out error))
                    {
                        return false;
                    }
                }

                if (kind == SpatialAggregationSummaryKind.Histogram)
                {
                    bins ??= DefaultHistogramBins;
                    if (bins <= 0 || bins > MaxHistogramBins ||
                        (min.HasValue && max.HasValue && min.Value >= max.Value))
                    {
                        error = StandardErrorHelpers.CreateBadRequest(
                            context,
                            "Invalid summaries parameter",
                            [$"Histogram summaries require 1-{MaxHistogramBins} bins and max greater than min when bounds are supplied."]);
                        return false;
                    }
                }

                builder.Add(new SpatialAggregationSummaryDefinition
                {
                    Id = id,
                    Kind = kind,
                    Title = title,
                    Field = field,
                    FieldType = !string.IsNullOrWhiteSpace(field) &&
                        TryResolveSpatialAggregationFieldType(resource, field, out var fieldType)
                            ? fieldType
                            : null,
                    Unit = unit,
                    CategoryLimit = Math.Min(limit.GetValueOrDefault(DefaultCategoryLimit), MaxCategoryLimit),
                    IncludeOther = includeOther,
                    CategoryOrderBy = orderBy,
                    HistogramBins = bins,
                    HistogramMin = min,
                    HistogramMax = max,
                    HistogramMethod = kind == SpatialAggregationSummaryKind.Histogram &&
                        TryReadStringProperty(element, "method", out var method)
                        ? method
                        : null,
                    Ranges = ranges
                });
            }

            summaries = builder.ToImmutable();
            return true;
        }
        catch (JsonException)
        {
            error = StandardErrorHelpers.CreateBadRequest(
                context,
                "Invalid summaries parameter",
                ["summaries must be valid JSON."]);
            return false;
        }
    }

    private static bool TryParseSpatialAggregationGroupBy(
        HttpContext context,
        IReadOnlyDictionary<string, StringValues> values,
        MetadataV2Resource resource,
        out ImmutableArray<ParsedGroupBy> groupBy,
        out IResult? error)
    {
        groupBy = ImmutableArray<ParsedGroupBy>.Empty;
        error = null;

        var raw = GetValueString(values, "groupBy");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!raw.TrimStart().StartsWith('['))
        {
            raw = $"[{raw}]";
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = StandardErrorHelpers.CreateBadRequest(
                    context,
                    "Invalid groupBy parameter",
                    ["groupBy must be a JSON array."]);
                return false;
            }

            var builder = ImmutableArray.CreateBuilder<ParsedGroupBy>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object ||
                    !TryReadStringProperty(element, "field", out var field) ||
                    string.IsNullOrWhiteSpace(field))
                {
                    error = StandardErrorHelpers.CreateBadRequest(
                        context,
                        "Invalid groupBy parameter",
                        ["Each groupBy entry requires a string field property."]);
                    return false;
                }

                if (!IsSafeSpatialAggregationFieldName(field) ||
                    !TryReadStringProperty(element, "alias", out var alias) ||
                    !TryReadStringProperty(element, "label", out var label) ||
                    !TryReadStringProperty(element, "nullLabel", out var nullLabel) ||
                    !TryReadPositiveInt(element, "limit", out var limit))
                {
                    error = StandardErrorHelpers.CreateBadRequest(
                        context,
                        "Invalid groupBy parameter",
                        ["groupBy fields, aliases, labels, and limits are invalid."]);
                    return false;
                }

                builder.Add(new ParsedGroupBy(field, alias, label, limit, nullLabel));
            }

            groupBy = builder.ToImmutable();
            return true;
        }
        catch (JsonException)
        {
            error = StandardErrorHelpers.CreateBadRequest(
                context,
                "Invalid groupBy parameter",
                ["groupBy must be valid JSON."]);
            return false;
        }
    }

    private static async Task<Dictionary<string, SpatialAggregationSummaryValueResponse>?> BuildSpatialAggregationTotalsAsync(
        IFeatureReader featureReader,
        MetadataV2Resource resource,
        int storageLayerId,
        FeatureQuery featureQuery,
        ImmutableArray<SpatialAggregationSummaryDefinition> summaries,
        List<SpatialAggregationDegradedReasonResponse> degraded,
        string sourceId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var totals = new Dictionary<string, SpatialAggregationSummaryValueResponse>(StringComparer.Ordinal);

        var metricStats = summaries
            .Where(summary => summary.Kind is SpatialAggregationSummaryKind.Count
                or SpatialAggregationSummaryKind.Sum
                or SpatialAggregationSummaryKind.Avg
                or SpatialAggregationSummaryKind.Min
                or SpatialAggregationSummaryKind.Max)
            .Select(summary => new StatisticDefinition
            {
                StatisticType = ToStatisticType(summary.Kind),
                OnStatisticField = string.IsNullOrWhiteSpace(summary.Field) ? FieldNames.ObjectId : summary.Field!,
                OutStatisticFieldName = summary.Id,
                FieldType = summary.FieldType
            })
            .ToImmutableArray();

        IReadOnlyDictionary<string, object?>? metricRow = null;
        if (!metricStats.IsDefaultOrEmpty)
        {
            var rows = await featureReader.QueryStatisticsAsync(
                storageLayerId,
                featureQuery with { OutStatistics = metricStats },
                cancellationToken);
            metricRow = rows.FirstOrDefault();
        }

        foreach (var summary in summaries)
        {
            switch (summary.Kind)
            {
                case SpatialAggregationSummaryKind.Count:
                case SpatialAggregationSummaryKind.Sum:
                case SpatialAggregationSummaryKind.Avg:
                case SpatialAggregationSummaryKind.Min:
                case SpatialAggregationSummaryKind.Max:
                    totals[summary.Id] = CreateMetricSummaryValue(summary, metricRow?.GetValueOrDefault(summary.Id));
                    break;
                case SpatialAggregationSummaryKind.Category:
                    totals[summary.Id] = await BuildCategoryTotalAsync(featureReader, storageLayerId, featureQuery, summary, cancellationToken);
                    break;
                case SpatialAggregationSummaryKind.Histogram:
                    totals[summary.Id] = await BuildHistogramTotalAsync(
                        featureReader,
                        resource,
                        storageLayerId,
                        featureQuery,
                        summary,
                        degraded,
                        sourceId,
                        context,
                        cancellationToken);
                    break;
                case SpatialAggregationSummaryKind.Range:
                    totals[summary.Id] = await BuildRangeTotalAsync(featureReader, resource, storageLayerId, featureQuery, summary, context, cancellationToken);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(summaries), summary.Kind, "Unsupported summary kind.");
            }
        }

        return totals;
    }

    private static async Task<SpatialAggregationSummaryValueResponse> BuildCategoryTotalAsync(
        IFeatureReader featureReader,
        int storageLayerId,
        FeatureQuery featureQuery,
        SpatialAggregationSummaryDefinition summary,
        CancellationToken cancellationToken)
    {
        var countAlias = $"{summary.Id}_count";
        var rows = await featureReader.QueryStatisticsAsync(
            storageLayerId,
            featureQuery with
            {
                GroupByFields = ImmutableArray.Create(summary.Field!),
                OutStatistics = ImmutableArray.Create(new StatisticDefinition
                {
                    StatisticType = StatisticType.Count,
                    OnStatisticField = FieldNames.ObjectId,
                    OutStatisticFieldName = countAlias
                })
            },
            cancellationToken);

        var valueField = summary.Field!;
        var buckets = rows
            .Where(row => row.TryGetValue(valueField, out var value) && value is not null)
            .Select(row => new
            {
                Value = row[valueField],
                Count = ToLong(row.GetValueOrDefault(countAlias))
            })
            .OrderByDescending(row => string.Equals(summary.CategoryOrderBy, "count-asc", StringComparison.OrdinalIgnoreCase) ? -row.Count : row.Count)
            .ThenBy(row => Convert.ToString(row.Value, CultureInfo.InvariantCulture), StringComparer.Ordinal)
            .Take(summary.CategoryLimit.GetValueOrDefault(DefaultCategoryLimit))
            .Select(row => new SpatialAggregationBucketValueResponse
            {
                Value = row.Value,
                Count = row.Count
            })
            .ToArray();

        var selected = new HashSet<string>(
            buckets.Select(bucket => Convert.ToString(bucket.Value, CultureInfo.InvariantCulture) ?? string.Empty),
            StringComparer.Ordinal);
        var otherCount = rows
            .Where(row => row.TryGetValue(valueField, out var value) && value is not null)
            .Where(row => !selected.Contains(Convert.ToString(row[valueField], CultureInfo.InvariantCulture) ?? string.Empty))
            .Sum(row => ToLong(row.GetValueOrDefault(countAlias)));
        var nullCount = rows
            .Where(row => !row.TryGetValue(valueField, out var value) || value is null)
            .Sum(row => ToLong(row.GetValueOrDefault(countAlias)));

        return new SpatialAggregationSummaryValueResponse
        {
            Kind = "category",
            Buckets = buckets,
            OtherCount = otherCount,
            NullCount = nullCount
        };
    }

    private static async Task<SpatialAggregationSummaryValueResponse> BuildHistogramTotalAsync(
        IFeatureReader featureReader,
        MetadataV2Resource resource,
        int storageLayerId,
        FeatureQuery featureQuery,
        SpatialAggregationSummaryDefinition summary,
        List<SpatialAggregationDegradedReasonResponse> degraded,
        string sourceId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var (min, max) = await ResolveHistogramBoundsAsync(featureReader, storageLayerId, featureQuery, summary, cancellationToken);

        if (!HasFiniteHistogramBounds(min, max))
        {
            degraded.Add(CreateDegraded(sourceId, $"Histogram summary '{summary.Id}' has no finite numeric range for the current filter."));
            return new SpatialAggregationSummaryValueResponse
            {
                Kind = "histogram",
                Buckets = []
            };
        }

        var minValue = min.GetValueOrDefault();
        var maxValue = max.GetValueOrDefault();
        var bins = summary.HistogramBins.GetValueOrDefault(DefaultHistogramBins);
        var width = (maxValue - minValue) / bins;
        var buckets = new SpatialAggregationBucketValueResponse[bins];
        for (var i = 0; i < bins; i++)
        {
            var lower = minValue + (width * i);
            var upper = i == bins - 1 ? maxValue : minValue + (width * (i + 1));
            var range = new SpatialAggregationRangeBucketDefinition
            {
                Id = i.ToString(CultureInfo.InvariantCulture),
                Min = lower,
                Max = upper,
                IncludeMin = true,
                IncludeMax = i == bins - 1
            };
            buckets[i] = new SpatialAggregationBucketValueResponse
            {
                Min = lower,
                Max = upper,
                Count = await CountRangeAsync(featureReader, resource, storageLayerId, featureQuery, summary.Field!, range, context, cancellationToken),
                IncludeMin = true,
                IncludeMax = i == bins - 1
            };
        }

        return new SpatialAggregationSummaryValueResponse
        {
            Kind = "histogram",
            Buckets = buckets
        };
    }

    private static async Task<(double? Min, double? Max)> ResolveHistogramBoundsAsync(
        IFeatureReader featureReader,
        int storageLayerId,
        FeatureQuery featureQuery,
        SpatialAggregationSummaryDefinition summary,
        CancellationToken cancellationToken)
    {
        var min = summary.HistogramMin;
        var max = summary.HistogramMax;
        if (min.HasValue && max.HasValue)
        {
            return (min, max);
        }

        var boundsRows = await featureReader.QueryStatisticsAsync(
            storageLayerId,
            featureQuery with
            {
                OutStatistics = ImmutableArray.Create(
                    new StatisticDefinition
                    {
                        StatisticType = StatisticType.Min,
                        OnStatisticField = summary.Field!,
                        OutStatisticFieldName = $"{summary.Id}_min",
                        FieldType = summary.FieldType
                    },
                    new StatisticDefinition
                    {
                        StatisticType = StatisticType.Max,
                        OnStatisticField = summary.Field!,
                        OutStatisticFieldName = $"{summary.Id}_max",
                        FieldType = summary.FieldType
                    })
            },
            cancellationToken);

        var bounds = boundsRows.FirstOrDefault();
        return (
            min ?? ToNullableDouble(bounds?.GetValueOrDefault($"{summary.Id}_min")),
            max ?? ToNullableDouble(bounds?.GetValueOrDefault($"{summary.Id}_max")));
    }

    private static bool HasFiniteHistogramBounds(double? min, double? max)
        => min.HasValue &&
            max.HasValue &&
            double.IsFinite(min.Value) &&
            double.IsFinite(max.Value) &&
            min.Value < max.Value;

    private static async Task<SpatialAggregationSummaryValueResponse> BuildRangeTotalAsync(
        IFeatureReader featureReader,
        MetadataV2Resource resource,
        int storageLayerId,
        FeatureQuery featureQuery,
        SpatialAggregationSummaryDefinition summary,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var ranges = summary.Ranges.GetValueOrDefault();
        var buckets = new SpatialAggregationBucketValueResponse[ranges.Length];
        for (var i = 0; i < ranges.Length; i++)
        {
            var range = ranges[i];
            buckets[i] = new SpatialAggregationBucketValueResponse
            {
                Id = range.Id,
                Label = range.Label,
                Min = range.Min,
                Max = range.Max,
                IncludeMin = range.IncludeMin.GetValueOrDefault(true),
                IncludeMax = range.IncludeMax.GetValueOrDefault(false),
                Count = await CountRangeAsync(featureReader, resource, storageLayerId, featureQuery, summary.Field!, range, context, cancellationToken)
            };
        }

        return new SpatialAggregationSummaryValueResponse
        {
            Kind = "range",
            Buckets = buckets
        };
    }

    private static async Task<long> CountRangeAsync(
        IFeatureReader featureReader,
        MetadataV2Resource? resource,
        int storageLayerId,
        FeatureQuery featureQuery,
        string field,
        SpatialAggregationRangeBucketDefinition range,
        HttpContext? context,
        CancellationToken cancellationToken)
    {
        var rangeExpression = BuildRangeFilterExpression(field, range);
        if (rangeExpression == null)
        {
            return await featureReader.CountAsync(storageLayerId, featureQuery, cancellationToken);
        }

        var filterService = context?.RequestServices.GetService<IFilterExpressionService>();
        if (filterService == null || resource is null)
        {
            return 0;
        }
        var translationResult = filterService.Translate(rangeExpression, resource);
        if (!translationResult.IsSuccess || translationResult.SqlFilter == null)
        {
            return 0;
        }

        var combinedFilter = CombineSqlFragments(featureQuery.SqlFilter, translationResult.SqlFilter);
        return await featureReader.CountAsync(
            storageLayerId,
            featureQuery with { Where = null, SqlFilter = combinedFilter },
            cancellationToken);
    }

    private static FilterExpression? BuildRangeFilterExpression(string field, SpatialAggregationRangeBucketDefinition range)
    {
        var property = new PropertyReference(field);
        FilterExpression? expression = null;

        if (range.Min.HasValue)
        {
            var op = range.IncludeMin.GetValueOrDefault(true)
                ? BinaryOperator.GreaterThanOrEqual
                : BinaryOperator.GreaterThan;
            expression = new BinaryExpression(property, op, new Literal(range.Min.Value, LiteralType.Number));
        }

        if (range.Max.HasValue)
        {
            var op = range.IncludeMax.GetValueOrDefault(false)
                ? BinaryOperator.LessThanOrEqual
                : BinaryOperator.LessThan;
            var upper = new BinaryExpression(property, op, new Literal(range.Max.Value, LiteralType.Number));
            expression = expression == null ? upper : new BinaryExpression(expression, BinaryOperator.And, upper);
        }

        return expression;
    }

    private static SqlFragment CombineSqlFragments(SqlFragment? first, SqlFragment second)
    {
        if (first == null)
        {
            return second;
        }

        var reindexedSecond = ReindexSqlParameters(second.Sql, first.Parameters.Count);
        return new SqlFragment(
            $"({first.Sql}) AND ({reindexedSecond})",
            first.Parameters.Concat(second.Parameters).ToArray());
    }

    private static string ReindexSqlParameters(string sql, int offset)
    {
        if (offset == 0)
        {
            return sql;
        }

        return SqlParameterRegex().Replace(
            sql,
            match =>
            {
                var index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                return $"@p{index + offset}";
            });
    }

    private static ImmutableArray<SpatialAggregationSummaryDefinition> ResolveCellSummaryDefinitions(
        ImmutableArray<SpatialAggregationSummaryDefinition> summaries,
        Dictionary<string, SpatialAggregationSummaryValueResponse>? totals)
    {
        if (totals == null || totals.Count == 0)
        {
            return summaries;
        }

        var builder = ImmutableArray.CreateBuilder<SpatialAggregationSummaryDefinition>(summaries.Length);
        foreach (var summary in summaries)
        {
            if (summary.Kind == SpatialAggregationSummaryKind.Category &&
                totals.TryGetValue(summary.Id, out var categoryTotal) &&
                categoryTotal.Buckets is { Length: > 0 } categoryBuckets)
            {
                builder.Add(summary with
                {
                    CategoryBuckets = categoryBuckets
                        .Select(bucket => new SpatialAggregationCategoryBucketDefinition
                        {
                            Value = Convert.ToString(bucket.Value, CultureInfo.InvariantCulture) ?? string.Empty,
                            Label = bucket.Label
                        })
                        .ToImmutableArray()
                });
                continue;
            }

            if (summary.Kind == SpatialAggregationSummaryKind.Histogram &&
                (!summary.HistogramMin.HasValue || !summary.HistogramMax.HasValue) &&
                totals.TryGetValue(summary.Id, out var histogramTotal) &&
                histogramTotal.Buckets is { Length: > 0 } histogramBuckets)
            {
                builder.Add(summary with
                {
                    HistogramMin = histogramBuckets[0].Min,
                    HistogramMax = histogramBuckets[^1].Max
                });
                continue;
            }

            builder.Add(summary);
        }

        return builder.ToImmutable();
    }

    private static async Task<ImmutableArray<SpatialAggregationSummaryDefinition>> ResolveHistogramCellSummaryBoundsAsync(
        IFeatureReader featureReader,
        int storageLayerId,
        FeatureQuery featureQuery,
        ImmutableArray<SpatialAggregationSummaryDefinition> summaries,
        List<SpatialAggregationDegradedReasonResponse> degraded,
        string sourceId,
        CancellationToken cancellationToken)
    {
        var builder = ImmutableArray.CreateBuilder<SpatialAggregationSummaryDefinition>(summaries.Length);
        foreach (var summary in summaries)
        {
            if (summary.Kind != SpatialAggregationSummaryKind.Histogram ||
                (summary.HistogramMin.HasValue && summary.HistogramMax.HasValue))
            {
                builder.Add(summary);
                continue;
            }

            var (min, max) = await ResolveHistogramBoundsAsync(featureReader, storageLayerId, featureQuery, summary, cancellationToken);
            if (!HasFiniteHistogramBounds(min, max))
            {
                degraded.Add(CreateDegraded(
                    sourceId,
                    $"Histogram summary '{summary.Id}' is omitted from cell summaries because it has no finite numeric range for the current filter."));
                continue;
            }

            builder.Add(summary with
            {
                HistogramMin = min,
                HistogramMax = max
            });
        }

        return builder.ToImmutable();
    }

    private static SpatialAggregationCellResponse CreateSpatialAggregationCell(
        IReadOnlyDictionary<string, object?> row,
        ImmutableArray<SpatialAggregationSummaryDefinition> summaries,
        int resolution)
    {
        var attributes = new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase);
        var id = Convert.ToString(attributes.GetValueOrDefault("cellIndex"), CultureInfo.InvariantCulture) ?? string.Empty;
        JsonElement? geometry = null;
        if (attributes.TryGetValue("cellGeometry", out var cellGeometry) &&
            cellGeometry is string geometryJson)
        {
            try
            {
                using var document = JsonDocument.Parse(geometryJson);
                geometry = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                geometry = null;
            }
        }

        var summaryValues = new Dictionary<string, SpatialAggregationSummaryValueResponse>(StringComparer.Ordinal);
        foreach (var summary in summaries)
        {
            if (attributes.TryGetValue(summary.Id, out var value))
            {
                summaryValues[summary.Id] = CreateSummaryValue(summary, value);
            }
        }

        return new SpatialAggregationCellResponse
        {
            Id = id,
            Resolution = resolution,
            Geometry = geometry,
            Summaries = summaryValues
        };
    }

    private static SpatialAggregationSummaryValueResponse CreateSummaryValue(
        SpatialAggregationSummaryDefinition summary,
        object? value)
    {
        return summary.Kind switch
        {
            SpatialAggregationSummaryKind.Count
                or SpatialAggregationSummaryKind.Sum
                or SpatialAggregationSummaryKind.Avg
                or SpatialAggregationSummaryKind.Min
                or SpatialAggregationSummaryKind.Max => CreateMetricSummaryValue(summary, value),
            SpatialAggregationSummaryKind.Category
                or SpatialAggregationSummaryKind.Histogram
                or SpatialAggregationSummaryKind.Range => ParseStructuredSummaryValue(summary, value),
            _ => throw new ArgumentOutOfRangeException(nameof(summary), summary.Kind, "Unsupported summary kind.")
        };
    }

    private static SpatialAggregationSummaryValueResponse CreateMetricSummaryValue(
        SpatialAggregationSummaryDefinition summary,
        object? value)
    {
        return new SpatialAggregationSummaryValueResponse
        {
            Kind = SummaryKindToJson(summary.Kind),
            Value = CreateMetricSummaryJsonValue(summary.Kind, value),
            Unit = summary.Unit
        };
    }

    private static JsonElement? CreateMetricSummaryJsonValue(SpatialAggregationSummaryKind kind, object? value)
    {
        if (kind == SpatialAggregationSummaryKind.Count)
        {
            return JsonSerializer.SerializeToElement(ToLong(value), FeatureServerJsonContext.Default.Int64);
        }

        var numericValue = ToNullableDouble(value);
        return numericValue.HasValue
            ? JsonSerializer.SerializeToElement(numericValue.Value, FeatureServerJsonContext.Default.Double)
            : null;
    }

    private static SpatialAggregationSummaryValueResponse ParseStructuredSummaryValue(
        SpatialAggregationSummaryDefinition summary,
        object? value)
    {
        if (value is not string json || string.IsNullOrWhiteSpace(json))
        {
            return new SpatialAggregationSummaryValueResponse
            {
                Kind = SummaryKindToJson(summary.Kind),
                Buckets = []
            };
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var buckets = root.TryGetProperty("buckets", out var bucketArray) &&
                bucketArray.ValueKind == JsonValueKind.Array
                ? bucketArray.EnumerateArray().Select(ParseBucketValue).ToArray()
                : [];

            return new SpatialAggregationSummaryValueResponse
            {
                Kind = SummaryKindToJson(summary.Kind),
                Buckets = buckets,
                OtherCount = root.TryGetProperty("otherCount", out var other) && other.TryGetInt64(out var otherCount)
                    ? otherCount
                    : null,
                NullCount = root.TryGetProperty("nullCount", out var nullElement) && nullElement.TryGetInt64(out var nullCount)
                    ? nullCount
                    : null
            };
        }
        catch (JsonException)
        {
            return new SpatialAggregationSummaryValueResponse
            {
                Kind = SummaryKindToJson(summary.Kind),
                Buckets = [],
                Approximate = true
            };
        }
    }

    private static SpatialAggregationBucketValueResponse ParseBucketValue(JsonElement bucket)
    {
        return new SpatialAggregationBucketValueResponse
        {
            Id = bucket.TryGetProperty("id", out var id) ? id.GetString() : null,
            Label = bucket.TryGetProperty("label", out var label) ? label.GetString() : null,
            Value = bucket.TryGetProperty("value", out var value) ? JsonScalarValue(value) : null,
            Count = bucket.TryGetProperty("count", out var count) && count.TryGetInt64(out var parsedCount)
                ? parsedCount
                : null,
            Min = bucket.TryGetProperty("min", out var min) && min.TryGetDouble(out var parsedMin) ? parsedMin : null,
            Max = bucket.TryGetProperty("max", out var max) && max.TryGetDouble(out var parsedMax) ? parsedMax : null,
            IncludeMin = bucket.TryGetProperty("includeMin", out var includeMin) &&
                includeMin.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? includeMin.GetBoolean()
                : null,
            IncludeMax = bucket.TryGetProperty("includeMax", out var includeMax) &&
                includeMax.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? includeMax.GetBoolean()
                : null
        };
    }

    private static async Task<SpatialAggregationGroupedSummaryResponse[]?> BuildGroupedSummariesAsync(
        IFeatureReader featureReader,
        int storageLayerId,
        FeatureQuery featureQuery,
        ImmutableArray<SpatialAggregationSummaryDefinition> summaries,
        ImmutableArray<ParsedGroupBy> groupBy,
        List<SpatialAggregationDegradedReasonResponse> degraded,
        string sourceId,
        CancellationToken cancellationToken)
    {
        var metricSummaries = summaries
            .Where(summary => summary.Kind is SpatialAggregationSummaryKind.Count
                or SpatialAggregationSummaryKind.Sum
                or SpatialAggregationSummaryKind.Avg
                or SpatialAggregationSummaryKind.Min
                or SpatialAggregationSummaryKind.Max)
            .ToImmutableArray();

        if (metricSummaries.Length != summaries.Length)
        {
            degraded.Add(CreateDegraded(
                sourceId,
                "Grouped category, histogram, and range summaries are not supported by the current FeatureServer queryH3 slice. Grouped results include metric summaries only."));
        }

        if (metricSummaries.IsDefaultOrEmpty)
        {
            return [];
        }

        var stats = metricSummaries
            .Select(summary => new StatisticDefinition
            {
                StatisticType = ToStatisticType(summary.Kind),
                OnStatisticField = string.IsNullOrWhiteSpace(summary.Field) ? FieldNames.ObjectId : summary.Field!,
                OutStatisticFieldName = summary.Id,
                FieldType = summary.FieldType
            })
            .ToImmutableArray();

        var rows = await featureReader.QueryStatisticsAsync(
            storageLayerId,
            featureQuery with
            {
                GroupByFields = groupBy.Select(group => group.Field).ToImmutableArray(),
                OutStatistics = stats
            },
            cancellationToken);

        var limit = groupBy.Select(group => group.Limit).Where(limit => limit.HasValue).Min() ?? 100;
        return rows
            .Take(limit)
            .Select(row =>
            {
                var key = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var group in groupBy)
                {
                    key[group.Alias ?? group.Field] = row.GetValueOrDefault(group.Field);
                }

                var rowSummaries = new Dictionary<string, SpatialAggregationSummaryValueResponse>(StringComparer.Ordinal);
                foreach (var summary in metricSummaries)
                {
                    rowSummaries[summary.Id] = CreateMetricSummaryValue(summary, row.GetValueOrDefault(summary.Id));
                }

                return new SpatialAggregationGroupedSummaryResponse
                {
                    Key = key,
                    Summaries = rowSummaries
                };
            })
            .ToArray();
    }

    private static SpatialAggregationMetadataResponse BuildSpatialAggregationMetadata(
        SpatialAggregationRequestPlan plan,
        ImmutableArray<SpatialAggregationSummaryDefinition> summaries,
        SpatialAggregationIndexModelResponse indexModel,
        int loadedCellCount,
        bool isPartial)
    {
        var summaryMetadata = summaries.Select(CreateSummaryMetadata).ToArray();
        var groupByMetadata = plan.GroupBy.IsDefaultOrEmpty
            ? null
            : plan.GroupBy.Select(group => new SpatialAggregationGroupByMetadataResponse
            {
                Field = group.Field,
                Alias = group.Alias,
                Title = group.Label
            }).ToArray();

        var widgets = summaryMetadata.Select(CreateSummaryWidget).ToList();
        if (groupByMetadata is { Length: > 0 })
        {
            widgets.Add(new SpatialAggregationWidgetMetadataResponse
            {
                Id = "grouped-summaries",
                Kind = "grouped-table",
                Title = "Grouped summaries",
                SummaryIds = summaryMetadata.Select(summary => summary.Id).ToArray(),
                GroupBy = groupByMetadata.Select(group => group.Alias ?? group.Field).ToArray(),
                Interactions = ["filter", "drilldown"],
                Progressive = new SpatialAggregationWidgetProgressiveResponse
                {
                    StableAcrossPages = false,
                    PartialValueSemantics = "refine"
                }
            });
        }

        return new SpatialAggregationMetadataResponse
        {
            SourceId = plan.SourceId,
            IndexModels = [indexModel],
            Summaries = summaryMetadata,
            GroupBy = groupByMetadata,
            Widgets = widgets.ToArray(),
            Progressive = new SpatialAggregationProgressiveStateResponse
            {
                Status = isPartial ? "partial" : "complete",
                Refinement = isPartial ? "append" : null,
                LoadedCellCount = loadedCellCount
            },
            Cache = new SpatialAggregationCacheMetadataResponse
            {
                MetadataCacheable = true,
                ResultCacheable = false,
                CacheKeyParts = [
                    SpatialAggregationContractConstants.FeatureServerProtocol,
                    plan.SourceId,
                    "queryH3"
                ]
            }
        };
    }

    private static SpatialAggregationSummaryMetadataResponse CreateSummaryMetadata(SpatialAggregationSummaryDefinition summary)
    {
        return new SpatialAggregationSummaryMetadataResponse
        {
            Id = summary.Id,
            Kind = SummaryKindToJson(summary.Kind),
            Title = summary.Title,
            Field = summary.Field,
            ValueType = summary.Kind == SpatialAggregationSummaryKind.Count ? "number" : FieldTypeToValueType(summary.FieldType),
            Unit = summary.Unit,
            Ranges = summary.Ranges.HasValue
                ? summary.Ranges.Value.Select(range => new SpatialAggregationRangeBucketMetadataResponse
                {
                    Id = range.Id,
                    Label = range.Label,
                    Min = range.Min,
                    Max = range.Max,
                    IncludeMin = range.IncludeMin,
                    IncludeMax = range.IncludeMax
                }).ToArray()
                : null,
            Histogram = summary.Kind == SpatialAggregationSummaryKind.Histogram
                ? new SpatialAggregationHistogramMetadataResponse
                {
                    Bins = summary.HistogramBins,
                    Min = summary.HistogramMin,
                    Max = summary.HistogramMax,
                    Method = string.IsNullOrWhiteSpace(summary.HistogramMethod)
                        ? "equal-interval"
                        : summary.HistogramMethod
                }
                : null
        };
    }

    private static SpatialAggregationWidgetMetadataResponse CreateSummaryWidget(SpatialAggregationSummaryMetadataResponse summary)
    {
        return new SpatialAggregationWidgetMetadataResponse
        {
            Id = $"{summary.Id}-widget",
            Kind = summary.Kind switch
            {
                "category" => "category-list",
                "histogram" => "histogram",
                "range" => "range-list",
                _ => "stat"
            },
            Title = summary.Title,
            SummaryId = summary.Id,
            Field = summary.Field,
            ValueType = summary.ValueType,
            Unit = summary.Unit,
            Interactions = summary.Kind == "count" ? ["highlight"] : ["filter", "highlight"],
            Progressive = new SpatialAggregationWidgetProgressiveResponse
            {
                StableAcrossPages = summary.Kind is not ("histogram" or "range"),
                PartialValueSemantics = "refine"
            }
        };
    }

    private static bool TryParseRangeBuckets(
        HttpContext context,
        JsonElement summary,
        out ImmutableArray<SpatialAggregationRangeBucketDefinition>? ranges,
        out IResult? error)
    {
        ranges = null;
        error = null;
        if (!summary.TryGetProperty("ranges", out var rangesElement) ||
            rangesElement.ValueKind != JsonValueKind.Array ||
            rangesElement.GetArrayLength() == 0)
        {
            error = StandardErrorHelpers.CreateBadRequest(
                context,
                "Invalid summaries parameter",
                ["Range summaries require a non-empty ranges array."]);
            return false;
        }

        var builder = ImmutableArray.CreateBuilder<SpatialAggregationRangeBucketDefinition>();
        foreach (var rangeElement in rangesElement.EnumerateArray())
        {
            if (rangeElement.ValueKind != JsonValueKind.Object ||
                !TryReadStringProperty(rangeElement, "id", out var id) ||
                string.IsNullOrWhiteSpace(id) ||
                !TryReadStringProperty(rangeElement, "label", out var label) ||
                !TryReadOptionalDouble(rangeElement, "min", out var min) ||
                !TryReadOptionalDouble(rangeElement, "max", out var max))
            {
                error = StandardErrorHelpers.CreateBadRequest(
                    context,
                    "Invalid summaries parameter",
                    ["Each range bucket requires an id and valid numeric min/max values when supplied."]);
                return false;
            }

            if (min.HasValue && max.HasValue && min.Value >= max.Value)
            {
                error = StandardErrorHelpers.CreateBadRequest(
                    context,
                    "Invalid summaries parameter",
                    [$"Range bucket '{id}' max must be greater than min."]);
                return false;
            }

            var includeMin = TryReadBool(rangeElement, "includeMin", out var parsedIncludeMin)
                ? parsedIncludeMin
                : (bool?)null;
            var includeMax = TryReadBool(rangeElement, "includeMax", out var parsedIncludeMax)
                ? parsedIncludeMax
                : (bool?)null;
            builder.Add(new SpatialAggregationRangeBucketDefinition
            {
                Id = id,
                Label = label,
                Min = min,
                Max = max,
                IncludeMin = includeMin,
                IncludeMax = includeMax
            });
        }

        ranges = builder.ToImmutable();
        return true;
    }

    private static bool TryParseSpatialAggregationInclude(
        IReadOnlyDictionary<string, StringValues> values,
        out bool includeCells,
        out bool includeTotals)
    {
        includeCells = true;
        includeTotals = true;
        var raw = GetValueString(values, "include");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (TryReadBool(document.RootElement, "cells", out var cells))
            {
                includeCells = cells;
            }

            if (TryReadBool(document.RootElement, "totals", out var totals))
            {
                includeTotals = totals;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParsePageLimit(
        HttpContext context,
        IReadOnlyDictionary<string, StringValues> values,
        out int? limit,
        out IResult? error)
    {
        limit = null;
        error = null;
        var raw = GetValueString(values, "page");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = StandardErrorHelpers.CreateBadRequest(
                    context,
                    "Invalid page parameter",
                    ["page must be a JSON object."]);
                return false;
            }

            if (!TryReadPositiveInt(document.RootElement, "limit", out limit))
            {
                error = StandardErrorHelpers.CreateBadRequest(
                    context,
                    "Invalid page parameter",
                    ["page.limit must be a positive integer."]);
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            error = StandardErrorHelpers.CreateBadRequest(
                context,
                "Invalid page parameter",
                ["page must be valid JSON."]);
            return false;
        }
    }

    private static bool TryParseSpatialAggregationSummaryKind(string raw, out SpatialAggregationSummaryKind kind)
    {
        kind = raw.ToLowerInvariant() switch
        {
            "count" => SpatialAggregationSummaryKind.Count,
            "sum" => SpatialAggregationSummaryKind.Sum,
            "avg" => SpatialAggregationSummaryKind.Avg,
            "min" => SpatialAggregationSummaryKind.Min,
            "max" => SpatialAggregationSummaryKind.Max,
            "category" => SpatialAggregationSummaryKind.Category,
            "histogram" => SpatialAggregationSummaryKind.Histogram,
            "range" => SpatialAggregationSummaryKind.Range,
            _ => default
        };

        return raw.ToLowerInvariant() is "count" or "sum" or "avg" or "min" or "max" or "category" or "histogram" or "range";
    }

    private static StatisticType ToStatisticType(SpatialAggregationSummaryKind kind) => kind switch
    {
        SpatialAggregationSummaryKind.Count => StatisticType.Count,
        SpatialAggregationSummaryKind.Sum => StatisticType.Sum,
        SpatialAggregationSummaryKind.Avg => StatisticType.Avg,
        SpatialAggregationSummaryKind.Min => StatisticType.Min,
        SpatialAggregationSummaryKind.Max => StatisticType.Max,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Summary kind is not a metric.")
    };

    private static string SummaryKindToJson(SpatialAggregationSummaryKind kind) => kind switch
    {
        SpatialAggregationSummaryKind.Count => "count",
        SpatialAggregationSummaryKind.Sum => "sum",
        SpatialAggregationSummaryKind.Avg => "avg",
        SpatialAggregationSummaryKind.Min => "min",
        SpatialAggregationSummaryKind.Max => "max",
        SpatialAggregationSummaryKind.Category => "category",
        SpatialAggregationSummaryKind.Histogram => "histogram",
        SpatialAggregationSummaryKind.Range => "range",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported summary kind.")
    };

    private static bool RequiresField(SpatialAggregationSummaryKind kind)
        => kind != SpatialAggregationSummaryKind.Count;

    private static bool TryReadStringProperty(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return true;
    }

    private static bool TryReadPositiveInt(JsonElement element, string propertyName, out int? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var intValue) && intValue > 0)
        {
            value = intValue;
            return true;
        }

        return false;
    }

    private static bool TryReadOptionalDouble(JsonElement element, string propertyName, out double? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out var doubleValue) &&
            double.IsFinite(doubleValue))
        {
            value = doubleValue;
            return true;
        }

        return false;
    }

    private static bool TryReadBool(JsonElement element, string propertyName, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    private static object? JsonScalarValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };
    }

    private static long ToLong(object? value)
    {
        return value switch
        {
            null => 0,
            long longValue => longValue,
            int intValue => intValue,
            double doubleValue when double.IsFinite(doubleValue) => Convert.ToInt64(doubleValue),
            decimal decimalValue => Convert.ToInt64(decimalValue, CultureInfo.InvariantCulture),
            string stringValue when long.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0
        };
    }

    private static double? ToNullableDouble(object? value)
    {
        return value switch
        {
            null => null,
            double doubleValue when double.IsFinite(doubleValue) => doubleValue,
            float floatValue when float.IsFinite(floatValue) => floatValue,
            decimal decimalValue => Convert.ToDouble(decimalValue, CultureInfo.InvariantCulture),
            long longValue => longValue,
            int intValue => intValue,
            string stringValue when double.TryParse(stringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) => parsed,
            _ => null
        };
    }

    private static string? FieldTypeToValueType<TFieldType>(TFieldType? fieldType)
        where TFieldType : struct, Enum
    {
        return fieldType?.ToString() switch
        {
            "Integer" or "BigInteger" or "Float" or "Double" => "number",
            "Boolean" => "boolean",
            "Date" => "date",
            "String" => "string",
            _ => null
        };
    }

    private static bool TryResolveSpatialAggregationFieldType(
        MetadataV2Resource resource,
        string fieldName,
        out MetadataV2FieldType fieldType)
    {
        var objectIdField = resource.FindPrimaryIdField();
        if (string.Equals(fieldName, objectIdField?.Name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fieldName, FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase))
        {
            fieldType = objectIdField?.Type ?? MetadataV2FieldType.Integer;
            return true;
        }

        foreach (var field in resource.SchemaFields)
        {
            if (string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                fieldType = field.Type;
                return true;
            }
        }

        fieldType = default;
        return false;
    }

    private static bool IsSafeSpatialAggregationFieldName(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return false;
        }

        if (!(char.IsAsciiLetter(fieldName[0]) || fieldName[0] == '_'))
        {
            return false;
        }

        for (var i = 1; i < fieldName.Length; i++)
        {
            var c = fieldName[i];
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static SpatialAggregationDegradedReasonResponse CreateDegraded(string sourceId, string reason)
    {
        return new SpatialAggregationDegradedReasonResponse
        {
            Capability = SpatialAggregationContractConstants.Capability,
            Protocol = SpatialAggregationContractConstants.FeatureServerProtocol,
            SourceId = sourceId,
            Reason = reason
        };
    }
}

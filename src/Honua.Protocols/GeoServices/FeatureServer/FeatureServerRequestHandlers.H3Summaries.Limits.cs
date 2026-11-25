// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Models;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Protocols.GeoServices.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    private readonly record struct SummaryItemParseInput
    {
        public required HttpContext Context { get; init; }
        public required MetadataV2Object Resource { get; init; }
        public required JsonElement Element { get; init; }
        public required HashSet<string> SeenIds { get; init; }
        public required int TotalBucketSubQueries { get; init; }
    }

    private readonly record struct SummaryItemParseResult
    {
        public SpatialAggregationSummaryDefinition? Definition { get; init; }
        public int BucketSubQueries { get; init; }
        public IResult? Error { get; init; }
    }

    private readonly record struct SummaryIdentityFields
    {
        public required string Id { get; init; }
        public required SpatialAggregationSummaryKind Kind { get; init; }
        public string? Field { get; init; }
        public string? Title { get; init; }
        public string? Unit { get; init; }
        public string? OrderBy { get; init; }
    }

    private readonly record struct CategoryOtherCountInput
    {
        public required IFeatureReader FeatureReader { get; init; }
        public required int StorageLayerId { get; init; }
        public required FeatureQuery FeatureQuery { get; init; }
        public required ImmutableArray<IReadOnlyDictionary<string, object?>> Rows { get; init; }
        public required SpatialAggregationBucketValueResponse[] Buckets { get; init; }
        public required string ValueField { get; init; }
        public required string CountAlias { get; init; }
        public required long NullCount { get; init; }
    }

    private static SummaryItemParseResult ParseSpatialAggregationSummaryItem(SummaryItemParseInput input)
    {
        var (identity, identityError) = ParseSummaryIdentityAndField(input.Context, input.Resource, (input.Element, input.SeenIds));
        if (identityError is not null)
        {
            return new SummaryItemParseResult { Error = identityError };
        }

        return CompleteSummaryItemParse(input, identity!.Value);
    }

    private static (SummaryIdentityFields? Fields, IResult? Error) ParseSummaryIdentityAndField(
        HttpContext context,
        MetadataV2Object resource,
        (JsonElement Element, HashSet<string> SeenIds) target)
    {
        var (element, seenIds) = target;
        if (element.ValueKind != JsonValueKind.Object ||
            !TryReadStringProperty(element, "id", out var id) ||
            !TryReadStringProperty(element, "kind", out var kindRaw) ||
            string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(kindRaw))
        {
            return (null, CreateSummaryBadRequest(context, "Each summary requires string id and kind properties."));
        }

        if (!seenIds.Add(id))
            return (null, CreateSummaryBadRequest(context, $"Duplicate summary id '{id}'."));
        if (!TryParseSpatialAggregationSummaryKind(kindRaw, out var kind))
            return (null, CreateSummaryBadRequest(context, $"Unsupported summary kind '{kindRaw}'."));

        if (!TryReadStringProperty(element, "field", out var field) ||
            !TryReadStringProperty(element, "title", out var title) ||
            !TryReadStringProperty(element, "unit", out var unit) ||
            !TryReadStringProperty(element, "orderBy", out var orderBy))
        {
            return (null, CreateSummaryBadRequest(context, "Summary string properties must be JSON strings when supplied."));
        }

        if (ValidateSummaryField(context, resource, (kind, kindRaw, field)) is { } fieldError)
            return (null, fieldError);

        return (new SummaryIdentityFields { Id = id, Kind = kind, Field = field, Title = title, Unit = unit, OrderBy = orderBy }, null);
    }

    private static IResult? ValidateSummaryField(
        HttpContext context,
        MetadataV2Object resource,
        (SpatialAggregationSummaryKind Kind, string KindRaw, string? Field) target)
    {
        if (RequiresField(target.Kind) && string.IsNullOrWhiteSpace(target.Field))
        {
            return CreateSummaryBadRequest(context, $"{target.KindRaw} summaries require a field.");
        }

        if (!string.IsNullOrWhiteSpace(target.Field) && !IsSafeSpatialAggregationFieldName(target.Field))
        {
            return CreateSummaryBadRequest(context, $"Invalid summary field '{target.Field}'.");
        }

        if (!string.IsNullOrWhiteSpace(target.Field) && !IsSchemaField(resource, target.Field))
        {
            return CreateSummaryBadRequest(context, $"Summary field '{target.Field}' does not exist on the layer.");
        }

        return null;
    }

    private static SummaryItemParseResult CompleteSummaryItemParse(
        SummaryItemParseInput input,
        SummaryIdentityFields identity)
    {
        var element = input.Element;
        if (!TryReadPositiveInt(element, "limit", out var limit) ||
            !TryReadPositiveInt(element, "bins", out var bins) ||
            !TryReadOptionalDouble(element, "min", out var min) ||
            !TryReadOptionalDouble(element, "max", out var max))
        {
            return new SummaryItemParseResult
            {
                Error = CreateSummaryBadRequest(input.Context, "Summary numeric properties must be finite positive values where required.")
            };
        }

        ImmutableArray<SpatialAggregationRangeBucketDefinition>? ranges = null;
        if (identity.Kind == SpatialAggregationSummaryKind.Range &&
            !TryParseRangeBuckets(input.Context, element, out ranges, out var rangeError))
        {
            return new SummaryItemParseResult { Error = rangeError };
        }

        var (bucketSubQueries, normalizedBins, bucketError) = ValidateSummaryBuckets(
            input.Context,
            identity.Kind,
            (bins, min, max, ranges, input.TotalBucketSubQueries));
        if (bucketError is not null)
        {
            return new SummaryItemParseResult { Error = bucketError };
        }

        var definition = BuildSummaryDefinition(input, identity, (limit, normalizedBins, min, max, ranges));
        return new SummaryItemParseResult { Definition = definition, BucketSubQueries = bucketSubQueries };
    }

    private static (int BucketSubQueries, int? Bins, IResult? Error) ValidateSummaryBuckets(
        HttpContext context,
        SpatialAggregationSummaryKind kind,
        (int? Bins, double? Min, double? Max, ImmutableArray<SpatialAggregationRangeBucketDefinition>? Ranges, int CurrentTotal) state)
    {
        var bins = state.Bins;
        var addedSubQueries = 0;
        if (kind == SpatialAggregationSummaryKind.Histogram)
        {
            bins ??= DefaultHistogramBins;
            if (bins <= 0 || bins > MaxHistogramBins ||
                (state.Min.HasValue && state.Max.HasValue && state.Min.Value >= state.Max.Value))
            {
                return (0, bins, CreateSummaryBadRequest(
                    context,
                    $"Histogram summaries require 1-{MaxHistogramBins} bins and max greater than min when bounds are supplied."));
            }

            addedSubQueries = bins.Value;
        }
        else if (kind == SpatialAggregationSummaryKind.Range && state.Ranges.HasValue)
        {
            addedSubQueries = state.Ranges.Value.Length;
        }

        if (state.CurrentTotal + addedSubQueries > MaxTotalSubQueriesPerRequest)
        {
            return (0, bins, CreateSummaryBadRequest(
                context,
                $"Total histogram and range bucket queries cannot exceed {MaxTotalSubQueriesPerRequest} per request."));
        }

        return (addedSubQueries, bins, null);
    }

    private static SpatialAggregationSummaryDefinition BuildSummaryDefinition(
        SummaryItemParseInput input,
        SummaryIdentityFields identity,
        (int? Limit, int? Bins, double? Min, double? Max, ImmutableArray<SpatialAggregationRangeBucketDefinition>? Ranges) options)
    {
        var includeOther = TryReadBool(input.Element, "includeOther", out var parsedIncludeOther) && parsedIncludeOther;
        return new SpatialAggregationSummaryDefinition
        {
            Id = identity.Id,
            Kind = identity.Kind,
            Title = identity.Title,
            Field = identity.Field,
            FieldType = !string.IsNullOrWhiteSpace(identity.Field) &&
                TryResolveSpatialAggregationFieldType(input.Resource, identity.Field, out var fieldType)
                    ? fieldType
                    : null,
            Unit = identity.Unit,
            CategoryLimit = Math.Min(options.Limit.GetValueOrDefault(DefaultCategoryLimit), MaxCategoryLimit),
            IncludeOther = includeOther,
            CategoryOrderBy = identity.OrderBy,
            HistogramBins = options.Bins,
            HistogramMin = options.Min,
            HistogramMax = options.Max,
            HistogramMethod = identity.Kind == SpatialAggregationSummaryKind.Histogram &&
                TryReadStringProperty(input.Element, "method", out var method)
                ? method
                : null,
            Ranges = options.Ranges
        };
    }

    private static IResult CreateSummaryBadRequest(HttpContext context, string message)
        => StandardErrorHelpers.CreateBadRequest(context, "Invalid summaries parameter", [message]);

    private static SpatialAggregationBucketValueResponse[] BuildCategoryBuckets(
        ImmutableArray<IReadOnlyDictionary<string, object?>> rows,
        SpatialAggregationSummaryDefinition summary,
        string countAlias)
    {
        var valueField = summary.Field!;
        var isCountAsc = string.Equals(summary.CategoryOrderBy, "count-asc", StringComparison.OrdinalIgnoreCase);
        return rows
            .Where(row => row.TryGetValue(valueField, out var value) && value is not null)
            .Select(row => new
            {
                Value = row[valueField],
                Count = ToLong(row.GetValueOrDefault(countAlias))
            })
            .OrderByDescending(row => isCountAsc ? -row.Count : row.Count)
            .ThenBy(row => Convert.ToString(row.Value, CultureInfo.InvariantCulture), StringComparer.Ordinal)
            .Take(summary.CategoryLimit.GetValueOrDefault(DefaultCategoryLimit))
            .Select(row => new SpatialAggregationBucketValueResponse
            {
                Value = row.Value,
                Count = row.Count
            })
            .ToArray();
    }

    private static async Task<long> ResolveCategoryOtherCountAsync(
        CategoryOtherCountInput input,
        CancellationToken cancellationToken)
    {
        if (input.Rows.Length >= MaxGroupedSummaryRows)
        {
            var totalCount = await input.FeatureReader.CountAsync(
                input.StorageLayerId,
                input.FeatureQuery,
                cancellationToken);
            var selectedSum = input.Buckets.Sum(static bucket => bucket.Count ?? 0L);
            return Math.Max(0L, totalCount - selectedSum - input.NullCount);
        }

        var selected = new HashSet<string>(
            input.Buckets.Select(bucket => Convert.ToString(bucket.Value, CultureInfo.InvariantCulture) ?? string.Empty),
            StringComparer.Ordinal);
        return input.Rows
            .Where(row => row.TryGetValue(input.ValueField, out var value) && value is not null)
            .Where(row => !selected.Contains(Convert.ToString(row[input.ValueField], CultureInfo.InvariantCulture) ?? string.Empty))
            .Sum(row => ToLong(row.GetValueOrDefault(input.CountAlias)));
    }
}

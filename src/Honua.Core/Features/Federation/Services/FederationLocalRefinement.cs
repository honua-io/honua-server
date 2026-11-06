// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Federation.Services;

// Local refinement is deterministic and does no I/O; attribute filters and exact spatial relationships are not applied here.
internal static class FederationLocalRefinement
{
    public static ImmutableArray<Feature> ApplyTemporalFilter(ImmutableArray<Feature> features, in TemporalFilter filter)
    {
        var start = filter.Start;
        var end = filter.End;
        var startProp = filter.PropertyName;
        var endProp = filter.EndPropertyName;

        var builder = ImmutableArray.CreateBuilder<Feature>(features.Length);
        foreach (var feature in features)
        {
            if (!TryGetTimestamp(feature, startProp, out var rowStart))
            {
                continue;
            }

            var rowEnd = rowStart;
            if (!string.IsNullOrEmpty(endProp) && TryGetTimestamp(feature, endProp!, out var parsedEnd))
            {
                rowEnd = parsedEnd;
            }

            // Interval-intersection: [rowStart, rowEnd] overlaps [start, end].
            if (end is { } e && rowStart > e)
            {
                continue;
            }

            if (start is { } s && rowEnd < s)
            {
                continue;
            }

            builder.Add(feature);
        }

        return builder.ToImmutable();
    }

    public static ImmutableArray<Feature> ApplyOrderBy(ImmutableArray<Feature> features, ImmutableArray<OrderByClause> orderBy)
    {
        if (orderBy.IsDefaultOrEmpty || features.Length <= 1)
        {
            return features;
        }

        // OrderBy/ThenBy is documented as a stable sort, which preserves remote order for ties.
        IOrderedEnumerable<Feature>? ordered = null;
        foreach (var clause in orderBy)
        {
            ordered = ordered is null
                ? Sort(features, clause)
                : Then(ordered, clause);
        }

        return ordered is null ? features : ordered.ToImmutableArray();

        static IOrderedEnumerable<Feature> Sort(IEnumerable<Feature> source, OrderByClause clause) =>
            clause.Ascending
                ? source.OrderBy(f => GetValue(f, clause.Field), new FederationValueComparer(clause.NullOrdering, ascending: true))
                : source.OrderByDescending(f => GetValue(f, clause.Field), new FederationValueComparer(clause.NullOrdering, ascending: false));

        static IOrderedEnumerable<Feature> Then(IOrderedEnumerable<Feature> source, OrderByClause clause) =>
            clause.Ascending
                ? source.ThenBy(f => GetValue(f, clause.Field), new FederationValueComparer(clause.NullOrdering, ascending: true))
                : source.ThenByDescending(f => GetValue(f, clause.Field), new FederationValueComparer(clause.NullOrdering, ascending: false));
    }

    public static ImmutableArray<Feature> ApplyPaging(ImmutableArray<Feature> features, int? offset, int? limit)
    {
        var skip = offset is > 0 ? offset.Value : 0;
        if (skip == 0 && limit is null)
        {
            return features;
        }

        if (skip >= features.Length)
        {
            return ImmutableArray<Feature>.Empty;
        }

        var remaining = features.Length - skip;
        var take = limit is { } l && l < remaining ? l : remaining;
        if (take <= 0)
        {
            return ImmutableArray<Feature>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<Feature>(take);
        for (var i = skip; i < skip + take; i++)
        {
            builder.Add(features[i]);
        }

        return builder.MoveToImmutable();
    }

    private static object? GetValue(in Feature feature, string field) =>
        feature.Attributes.TryGetValue(field, out var value) ? value : null;

    internal static int CompareValues(
        object? left,
        object? right,
        NullOrdering nullOrdering,
        bool ascending) =>
        new FederationValueComparer(nullOrdering, ascending).Compare(left, right);

    private static bool TryGetTimestamp(in Feature feature, string field, out DateTimeOffset value)
    {
        value = default;
        if (!feature.Attributes.TryGetValue(field, out var raw) || raw is null)
        {
            return false;
        }

        switch (raw)
        {
            case DateTimeOffset dto:
                value = dto;
                return true;
            case DateTime dt:
                value = new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt);
                return true;
            case string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed):
                value = parsed;
                return true;
            default:
                return false;
        }
    }

    private sealed class FederationValueComparer(NullOrdering nullOrdering, bool ascending) : IComparer<object?>
    {
        public int Compare(object? x, object? y)
        {
            if (x is null && y is null)
            {
                return 0;
            }

            if (x is null)
            {
                return NullSortValue();
            }

            if (y is null)
            {
                return -NullSortValue();
            }

            if (TryAsDouble(x, out var dx) && TryAsDouble(y, out var dy))
            {
                return dx.CompareTo(dy);
            }

            if (TryAsDateTimeOffset(x, out var tx) && TryAsDateTimeOffset(y, out var ty))
            {
                return tx.CompareTo(ty);
            }

            return string.CompareOrdinal(
                Convert.ToString(x, CultureInfo.InvariantCulture),
                Convert.ToString(y, CultureInfo.InvariantCulture));
        }

        // The framework inverts this comparer's result for OrderByDescending, so the value we
        // return for a null operand must pre-compensate for that flip to land nulls in the
        // intended final position.
        private int NullSortValue()
        {
            // Final placement we want for a null vs a non-null value.
            var nullsFirstInFinalOrder = nullOrdering switch
            {
                NullOrdering.NullsFirst => true,
                NullOrdering.NullsLast => false,
                // PostgreSQL-style default: nulls last when ascending, nulls first when descending.
                _ => !ascending,
            };

            // Ascending uses the result as-is; descending inverts it.
            return ascending
                ? (nullsFirstInFinalOrder ? -1 : 1)
                : (nullsFirstInFinalOrder ? 1 : -1);
        }

        private static bool TryAsDouble(object value, out double result)
        {
            switch (value)
            {
                case sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
                    result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    return true;
                default:
                    result = 0;
                    return false;
            }
        }

        private static bool TryAsDateTimeOffset(object value, out DateTimeOffset result)
        {
            switch (value)
            {
                case DateTimeOffset dto:
                    result = dto;
                    return true;
                case DateTime dt:
                    result = new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt);
                    return true;
                default:
                    result = default;
                    return false;
            }
        }
    }
}

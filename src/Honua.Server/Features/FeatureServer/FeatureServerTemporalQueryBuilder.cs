// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Helpers;

namespace Honua.Server.Features.FeatureServer;

/// <summary>
/// Builds temporal filter expressions for FeatureServer time queries.
/// </summary>
internal static class FeatureServerTemporalQueryBuilder
{
    internal enum TimeRelation
    {
        Intersects,
        Overlaps,
        Within,
        Contains,
        Disjoint,
        Before,
        After,
        Equals,
        Starts,
        StartedBy,
        Finishes,
        FinishedBy,
        Meets,
        MetBy,
        OverlapsStartWithinEnd,
        OverlapsEndWithinStart
    }

    /// <summary>
    /// Builds a temporal filter expression from raw time values.
    /// </summary>
    internal static FilterExpression? BuildTemporalExpression(string? time, string? timeRelation, LayerDefinition layer)
    {
        if (string.IsNullOrWhiteSpace(time))
        {
            return null;
        }

        var queryParams = new QueryParameters
        {
            Time = time,
            TimeRelation = timeRelation
        };

        return BuildTemporalExpression(queryParams, layer);
    }

    /// <summary>
    /// Builds a temporal filter expression for FeatureServer time queries.
    /// </summary>
    internal static FilterExpression? BuildTemporalExpression(QueryParameters queryParams, LayerDefinition layer)
    {
        if (string.IsNullOrWhiteSpace(queryParams.Time))
        {
            return null;
        }

        var selection = TemporalExtentHelpers.ResolveTemporalFieldsOrThrow(layer);
        if (!TryParseTimeParameter(queryParams.Time, out var startTime, out var endTime))
        {
            throw new ArgumentException($"Invalid time parameter format: {queryParams.Time}");
        }

        var relation = ParseTimeRelation(queryParams.TimeRelation);
        var temporalType = selection.StartField.Type;
        var queryStart = ToTemporalLiteral(startTime, temporalType);
        var queryEnd = ToTemporalLiteral(endTime, temporalType);

        var startExpression = new PropertyReference(selection.StartField.Name);
        FilterExpression endExpression = selection.EndField == null
            ? startExpression
            : new FunctionCall(
                "COALESCE",
                new FilterExpression[]
                {
                    new PropertyReference(selection.EndField.Name),
                    startExpression
                });

        return BuildTemporalRelationExpression(relation, startExpression, endExpression, queryStart, queryEnd);
    }

    internal static TimeRelation ParseTimeRelation(string? timeRelation)
    {
        if (string.IsNullOrWhiteSpace(timeRelation))
        {
            return TimeRelation.Intersects;
        }

        return timeRelation.Trim().ToLowerInvariant() switch
        {
            "esritimerelationintersects" or "intersects" => TimeRelation.Intersects,
            "esritimerelationoverlaps" or "overlaps" => TimeRelation.Overlaps,
            "esritimerelationwithin" or "within" => TimeRelation.Within,
            "esritimerelationcontains" or "contains" => TimeRelation.Contains,
            "esritimerelationdisjoint" or "disjoint" => TimeRelation.Disjoint,
            "esritimerelationbefore" or "before" => TimeRelation.Before,
            "esritimerelationafter" or "after" => TimeRelation.After,
            "esritimerelationequals" or "equals" => TimeRelation.Equals,
            "esritimerelationstarts" or "starts" => TimeRelation.Starts,
            "esritimerelationstartedby" or "startedby" => TimeRelation.StartedBy,
            "esritimerelationfinishes" or "finishes" => TimeRelation.Finishes,
            "esritimerelationfinishedby" or "finishedby" => TimeRelation.FinishedBy,
            "esritimerelationmeets" or "meets" => TimeRelation.Meets,
            "esritimerelationmetby" or "metby" => TimeRelation.MetBy,
            "esritimerelationoverlapsstartwithinend" or "overlapsstartwithinend" => TimeRelation.OverlapsStartWithinEnd,
            "esritimerelationoverlapsendwithinstart" or "overlapsendwithinstart" => TimeRelation.OverlapsEndWithinStart,
            _ => throw new ArgumentException($"Unsupported timeRelation '{timeRelation}'.")
        };
    }

    internal static FilterExpression? BuildTemporalRelationExpression(
        TimeRelation relation,
        FilterExpression startExpression,
        FilterExpression endExpression,
        Literal? queryStart,
        Literal? queryEnd)
    {
        var startLessThan = Compare(endExpression, BinaryOperator.LessThan, queryStart);
        var startGreaterThan = Compare(startExpression, BinaryOperator.GreaterThan, queryEnd);
        var disjoint = Or(startLessThan, startGreaterThan);

        return relation switch
        {
            TimeRelation.Intersects => disjoint == null ? null : new UnaryExpression(UnaryOperator.Not, disjoint),
            TimeRelation.Disjoint => disjoint,
            TimeRelation.Before => CompareRequired(endExpression, BinaryOperator.LessThan, queryStart, relation, "start"),
            TimeRelation.After => CompareRequired(startExpression, BinaryOperator.GreaterThan, queryEnd, relation, "end"),
            TimeRelation.Equals => AndRequired(
                CompareRequired(startExpression, BinaryOperator.Equal, queryStart, relation, "start"),
                CompareRequired(endExpression, BinaryOperator.Equal, queryEnd, relation, "end"),
                relation),
            TimeRelation.Contains => AndRequired(
                CompareRequired(startExpression, BinaryOperator.LessThan, queryStart, relation, "start"),
                CompareRequired(endExpression, BinaryOperator.GreaterThan, queryEnd, relation, "end"),
                relation),
            TimeRelation.Within => AndRequired(
                CompareRequired(startExpression, BinaryOperator.GreaterThan, queryStart, relation, "start"),
                CompareRequired(endExpression, BinaryOperator.LessThan, queryEnd, relation, "end"),
                relation),
            TimeRelation.Starts => AndRequired(
                CompareRequired(startExpression, BinaryOperator.Equal, queryStart, relation, "start"),
                CompareRequired(endExpression, BinaryOperator.LessThan, queryEnd, relation, "end"),
                relation),
            TimeRelation.StartedBy => AndRequired(
                CompareRequired(startExpression, BinaryOperator.Equal, queryStart, relation, "start"),
                CompareRequired(endExpression, BinaryOperator.GreaterThan, queryEnd, relation, "end"),
                relation),
            TimeRelation.Finishes => AndRequired(
                CompareRequired(endExpression, BinaryOperator.Equal, queryEnd, relation, "end"),
                CompareRequired(startExpression, BinaryOperator.GreaterThan, queryStart, relation, "start"),
                relation),
            TimeRelation.FinishedBy => AndRequired(
                CompareRequired(endExpression, BinaryOperator.Equal, queryEnd, relation, "end"),
                CompareRequired(startExpression, BinaryOperator.LessThan, queryStart, relation, "start"),
                relation),
            TimeRelation.Meets => CompareRequired(endExpression, BinaryOperator.Equal, queryStart, relation, "start"),
            TimeRelation.MetBy => CompareRequired(startExpression, BinaryOperator.Equal, queryEnd, relation, "end"),
            TimeRelation.Overlaps => Or(
                BuildOverlapStartWithinEnd(startExpression, endExpression, queryStart, queryEnd, relation),
                BuildOverlapEndWithinStart(startExpression, endExpression, queryStart, queryEnd, relation)),
            TimeRelation.OverlapsStartWithinEnd => BuildOverlapStartWithinEnd(startExpression, endExpression, queryStart, queryEnd, relation),
            TimeRelation.OverlapsEndWithinStart => BuildOverlapEndWithinStart(startExpression, endExpression, queryStart, queryEnd, relation),
            _ => throw new ArgumentException($"Unsupported timeRelation '{relation}'.")
        };
    }

    internal static BinaryExpression? BuildOverlapStartWithinEnd(
        FilterExpression startExpression,
        FilterExpression endExpression,
        Literal? queryStart,
        Literal? queryEnd,
        TimeRelation relation)
    {
        return AndRequired(
            CompareRequired(startExpression, BinaryOperator.LessThan, queryStart, relation, "start"),
            AndRequired(
                CompareRequired(endExpression, BinaryOperator.GreaterThan, queryStart, relation, "start"),
                CompareRequired(endExpression, BinaryOperator.LessThan, queryEnd, relation, "end"),
                relation),
            relation);
    }

    internal static BinaryExpression? BuildOverlapEndWithinStart(
        FilterExpression startExpression,
        FilterExpression endExpression,
        Literal? queryStart,
        Literal? queryEnd,
        TimeRelation relation)
    {
        return AndRequired(
            CompareRequired(startExpression, BinaryOperator.GreaterThan, queryStart, relation, "start"),
            AndRequired(
                CompareRequired(startExpression, BinaryOperator.LessThan, queryEnd, relation, "end"),
                CompareRequired(endExpression, BinaryOperator.GreaterThan, queryEnd, relation, "end"),
                relation),
            relation);
    }

    internal static BinaryExpression? Compare(FilterExpression left, BinaryOperator op, Literal? right)
    {
        if (right == null)
        {
            return null;
        }

        return new BinaryExpression(left, op, right);
    }

    internal static BinaryExpression CompareRequired(
        FilterExpression left,
        BinaryOperator op,
        Literal? right,
        TimeRelation relation,
        string requiredPart)
    {
        if (right == null)
        {
            throw new ArgumentException($"timeRelation '{relation}' requires a {requiredPart} time value.");
        }

        return new BinaryExpression(left, op, right);
    }

    internal static BinaryExpression AndRequired(FilterExpression left, FilterExpression right, TimeRelation relation)
    {
        _ = relation;
        return new BinaryExpression(left, BinaryOperator.And, right);
    }

    internal static FilterExpression? Or(FilterExpression? left, FilterExpression? right)
    {
        if (left == null)
        {
            return right;
        }

        if (right == null)
        {
            return left;
        }

        return new BinaryExpression(left, BinaryOperator.Or, right);
    }

    internal static Literal? ToTemporalLiteral(DateTimeOffset? value, FieldType fieldType)
    {
        if (!value.HasValue)
        {
            return null;
        }

        if (fieldType == FieldType.Date)
        {
            return new Literal(DateOnly.FromDateTime(value.Value.UtcDateTime), LiteralType.Date);
        }

        return new Literal(value.Value, LiteralType.DateTime);
    }

    /// <summary>
    /// Parses time parameter string into start/end times.
    /// Supports Unix timestamps in milliseconds, ISO 8601, and open intervals using null/empty.
    /// </summary>
    internal static bool TryParseTimeParameter(string timeParam, out DateTimeOffset? start, out DateTimeOffset? end)
    {
        start = null;
        end = null;

        if (string.IsNullOrWhiteSpace(timeParam))
        {
            return false;
        }

        if (timeParam.Contains(','))
        {
            var parts = timeParam.Split(',', 2, StringSplitOptions.None);
            if (parts.Length != 2)
            {
                return false;
            }

            if (!TryParseOptionalTime(parts[0].Trim(), out start))
            {
                return false;
            }

            if (!TryParseOptionalTime(parts[1].Trim(), out end))
            {
                return false;
            }

            if (!start.HasValue && !end.HasValue)
            {
                return false;
            }

            if (start.HasValue && end.HasValue && start.Value > end.Value)
            {
                return false;
            }

            return true;
        }

        if (!TryParseSingleTime(timeParam, out start))
        {
            return false;
        }

        end = start;
        return true;
    }

    internal static bool TryParseOptionalTime(string timeValue, out DateTimeOffset? time)
    {
        time = null;

        if (string.IsNullOrWhiteSpace(timeValue) ||
            string.Equals(timeValue, "null", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return TryParseSingleTime(timeValue, out time);
    }

    /// <summary>
    /// Parses a single time value (Unix timestamp or ISO 8601)
    /// </summary>
    internal static bool TryParseSingleTime(string timeValue, out DateTimeOffset? time)
    {
        time = null;

        if (string.IsNullOrWhiteSpace(timeValue))
        {
            return false;
        }

        if (long.TryParse(timeValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixMs))
        {
            try
            {
                time = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
                return true;
            }
            catch
            {
                return false;
            }
        }

        if (DateTimeOffset.TryParse(
            timeValue,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsedTime))
        {
            time = parsedTime;
            return true;
        }

        return false;
    }
}

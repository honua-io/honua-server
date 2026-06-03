// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Helpers;

namespace Honua.Protocols.GeoServices;

/// <summary>
/// Builds temporal filter expressions for GeoServices time queries.
/// </summary>
internal static class GeoServicesTemporalQueryBuilder
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
    /// Builds a temporal filter expression from raw time values against a Metadata v2
    /// canonical resource. The resource must be configured as opt-in time-aware
    /// (start-time field declared on the temporal extension and resolving to a
    /// date/datetime schema field) before a non-empty time= filter is honored.
    /// When the resource is not time-enabled the time= filter is IGNORED and null
    /// is returned (Esri ignores time on non-time-aware layers rather than erroring).
    /// The <c>time=null,null</c> no-op form returns null without throwing.
    /// </summary>
    internal static FilterExpression? BuildTemporalExpression(
        string? time,
        string? timeRelation,
        MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (string.IsNullOrWhiteSpace(time))
        {
            return null;
        }

        return BuildTemporalExpressionCoreV2(time, timeRelation, resource);
    }

    private static FilterExpression? BuildTemporalExpressionCoreV2(
        string time,
        string? timeRelation,
        MetadataV2Resource resource)
    {
        if (!TryParseTimeParameter(time, out var startTime, out var endTime))
        {
            throw new ArgumentException($"Invalid time parameter format: {time}");
        }

        if (startTime is null && endTime is null)
        {
            return null;
        }

        if (!TemporalExtentHelpers.TryResolveOptInTemporalFieldsV2(resource, out var selection))
        {
            // Esri behavior: a time= parameter supplied against a layer that is not
            // time-enabled (no timeInfo / no opt-in temporal fields) is IGNORED rather
            // than rejected. Return null so the query proceeds with no temporal predicate
            // instead of surfacing a 400 "Invalid time parameter".
            return null;
        }

        var startField = FindV2Field(resource, selection.StartFieldName)
            ?? throw new ArgumentException(
                $"Resource '{resource.Metadata.Name}' temporal start field '{selection.StartFieldName}' is not present in the schema.");
        MetadataV2Field? endField = null;
        if (!string.IsNullOrWhiteSpace(selection.EndFieldName))
        {
            endField = FindV2Field(resource, selection.EndFieldName!);
        }

        var relation = ParseTimeRelation(timeRelation);
        var queryStart = ToTemporalLiteralV2(startTime, startField.Type);
        var queryEnd = ToTemporalLiteralV2(endTime, startField.Type);

        var startExpression = new PropertyReference(startField.Name);
        FilterExpression endExpression = endField == null
            ? startExpression
            : new FunctionCall(
                "COALESCE",
                new FilterExpression[]
                {
                    new PropertyReference(endField.Name),
                    startExpression
                });

        return BuildTemporalRelationExpression(relation, startExpression, endExpression, queryStart, queryEnd);
    }

    private static MetadataV2Field? FindV2Field(MetadataV2Resource resource, string fieldName)
    {
        foreach (var field in resource.SchemaFields)
        {
            if (string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                return field;
            }
        }
        return null;
    }

    private static Literal? ToTemporalLiteralV2(DateTimeOffset? value, MetadataV2FieldType fieldType)
    {
        if (!value.HasValue)
        {
            return null;
        }

        if (fieldType == MetadataV2FieldType.Date)
        {
            return new Literal(DateOnly.FromDateTime(value.Value.UtcDateTime), LiteralType.Date);
        }

        return new Literal(value.Value, LiteralType.DateTime);
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

            // Both bounds null is the documented "no temporal filter" form
            // (docs/gis/temporal-animation-api.md): time=null,null parses as a
            // valid no-op rather than an error so callers can treat it the
            // same as omitting the parameter. Callers must still check
            // (start, end) before constructing a real filter.
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

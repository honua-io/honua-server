// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Core.Queries.Filters.Cql2;

/// <summary>
/// Parses CQL2-JSON filter expressions into the shared AST representation.
/// </summary>
public sealed class Cql2JsonParser
{
    private const int DefaultSrid = 4326;

    /// <summary>
    /// Parses a CQL2-JSON filter expression into a <see cref="FilterExpression"/> AST.
    /// </summary>
    /// <param name="cql2Json">CQL2-JSON string payload.</param>
    /// <returns>Parsed filter expression.</returns>
    /// <exception cref="ArgumentException">Thrown when the JSON is invalid or unsupported.</exception>
    public FilterExpression Parse(string cql2Json)
    {
        if (string.IsNullOrWhiteSpace(cql2Json))
        {
            throw new ArgumentException("CQL2-JSON expression cannot be null or empty", nameof(cql2Json));
        }

        try
        {
            using var document = JsonDocument.Parse(cql2Json);
            return ParseExpression(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Invalid CQL2-JSON payload: {ex.Message}", nameof(cql2Json), ex);
        }
    }

    private FilterExpression ParseExpression(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                return ParseObjectExpression(element);
            case JsonValueKind.Array:
                return ParseArrayLiteral(element);
            case JsonValueKind.String:
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return ParseLiteral(element);
            default:
                throw new ArgumentException($"Unsupported JSON token in CQL2-JSON: {element.ValueKind}");
        }
    }

    private FilterExpression ParseObjectExpression(JsonElement element)
    {
        if (element.TryGetProperty("op", out var opElement))
        {
            var opValue = opElement.GetString();
            if (string.IsNullOrWhiteSpace(opValue))
            {
                throw new ArgumentException("CQL2-JSON operator cannot be empty");
            }

            var args = element.TryGetProperty("args", out var argsElement)
                ? argsElement
                : default;

            return ParseOperation(opValue, args);
        }

        if (element.TryGetProperty("property", out var propertyElement))
        {
            var propertyName = propertyElement.GetString();
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                throw new ArgumentException("CQL2-JSON property reference must be a non-empty string");
            }

            return new PropertyReference(propertyName);
        }

        if (TryParseTemporalLiteral(element, out var temporalLiteral))
        {
            return temporalLiteral!;
        }

        if (TryParseIntervalLiteral(element, out var intervalLiteral))
        {
            return intervalLiteral!;
        }

        if (LooksLikeGeometry(element) || LooksLikeBbox(element))
        {
            return ParseGeometryLiteral(element);
        }

        if (element.TryGetProperty("value", out var valueElement))
        {
            return ParseLiteral(valueElement);
        }

        throw new ArgumentException("Unsupported CQL2-JSON object expression");
    }

    private FilterExpression ParseOperation(string opValue, JsonElement argsElement)
    {
        var normalized = opValue.Trim();
        var normalizedLower = normalized.ToLowerInvariant();

        if (normalizedLower is "and" or "or")
        {
            var expressions = ParseArguments(argsElement);
            if (expressions.Count < 2)
            {
                throw new ArgumentException($"Operator '{opValue}' requires at least two arguments");
            }

            var binaryOperator = normalizedLower == "and" ? BinaryOperator.And : BinaryOperator.Or;
            var current = expressions[0];
            for (var i = 1; i < expressions.Count; i++)
            {
                current = new BinaryExpression(current, binaryOperator, expressions[i]);
            }

            return current;
        }

        if (normalizedLower == "not")
        {
            var expressions = ParseArguments(argsElement);
            if (expressions.Count != 1)
            {
                throw new ArgumentException("Operator 'not' requires a single argument");
            }

            return new UnaryExpression(UnaryOperator.Not, expressions[0]);
        }

        if (normalizedLower is "isnull" or "isnotnull")
        {
            var expressions = ParseArguments(argsElement);
            if (expressions.Count != 1)
            {
                throw new ArgumentException($"Operator '{opValue}' requires a single argument");
            }

            var unaryOperator = normalizedLower == "isnull" ? UnaryOperator.IsNull : UnaryOperator.IsNotNull;
            return new UnaryExpression(unaryOperator, expressions[0]);
        }

        if (normalizedLower is "in" or "not in" or "notin")
        {
            var expressions = ParseArguments(argsElement);
            if (expressions.Count != 2)
            {
                throw new ArgumentException($"Operator '{opValue}' requires two arguments");
            }

            var list = ExtractValueList(expressions[1]);
            var op = normalizedLower == "in" ? BinaryOperator.In : BinaryOperator.NotIn;
            return new BinaryExpression(expressions[0], op, list);
        }

        if (normalizedLower is "like" or "not like" or "notlike")
        {
            var expressions = ParseArguments(argsElement);
            if (expressions.Count != 2)
            {
                throw new ArgumentException($"Operator '{opValue}' requires two arguments");
            }

            var op = normalizedLower == "like" ? BinaryOperator.Like : BinaryOperator.NotLike;
            return new BinaryExpression(expressions[0], op, expressions[1]);
        }

        if (normalizedLower is "between" or "not between" or "notbetween")
        {
            var expressions = ParseArguments(argsElement);
            if (expressions.Count != 3)
            {
                throw new ArgumentException($"Operator '{opValue}' requires three arguments");
            }

            var negate = normalizedLower.StartsWith("not", StringComparison.Ordinal);
            return FilterExpressionHelpers.BuildBetweenExpression(expressions[0], expressions[1], expressions[2], negate);
        }

        if (normalizedLower is "=" or "==" or "!=" or "<>" or "<" or "<=" or ">" or ">=")
        {
            var expressions = ParseArguments(argsElement);
            if (expressions.Count != 2)
            {
                throw new ArgumentException($"Operator '{opValue}' requires two arguments");
            }

            var op = normalizedLower switch
            {
                "=" or "==" => BinaryOperator.Equal,
                "!=" or "<>" => BinaryOperator.NotEqual,
                "<" => BinaryOperator.LessThan,
                "<=" => BinaryOperator.LessThanOrEqual,
                ">" => BinaryOperator.GreaterThan,
                ">=" => BinaryOperator.GreaterThanOrEqual,
                _ => BinaryOperator.Equal
            };

            return new BinaryExpression(expressions[0], op, expressions[1]);
        }

        if (normalizedLower.StartsWith("s_", StringComparison.Ordinal))
        {
            var expressions = ParseArguments(argsElement);
            var spatialOperator = normalizedLower switch
            {
                "s_intersects" => SpatialOperator.Intersects,
                "s_contains" => SpatialOperator.Contains,
                "s_within" => SpatialOperator.Within,
                "s_crosses" => SpatialOperator.Crosses,
                "s_touches" => SpatialOperator.Touches,
                "s_overlaps" => SpatialOperator.Overlaps,
                "s_disjoint" => SpatialOperator.Disjoint,
                "s_equals" => SpatialOperator.Equals,
                "s_dwithin" => SpatialOperator.DWithin,
                "s_beyond" => SpatialOperator.Beyond,
                _ => throw new ArgumentException($"Unsupported spatial operator '{opValue}'")
            };

            if (spatialOperator is SpatialOperator.DWithin or SpatialOperator.Beyond)
            {
                if (expressions.Count != 3)
                {
                    throw new ArgumentException($"Spatial operator '{opValue}' requires three arguments");
                }

                return new SpatialDistancePredicate(spatialOperator, expressions[0], expressions[1], expressions[2]);
            }

            if (expressions.Count != 2)
            {
                throw new ArgumentException($"Spatial operator '{opValue}' requires two arguments");
            }

            return new SpatialPredicate(spatialOperator, expressions[0], expressions[1]);
        }

        if (normalizedLower.StartsWith("t_", StringComparison.Ordinal))
        {
            var expressions = ParseArguments(argsElement);
            if (expressions.Count != 2)
            {
                throw new ArgumentException($"Temporal operator '{opValue}' requires two arguments");
            }

            var temporalOperator = normalizedLower switch
            {
                "t_after" => TemporalOperator.After,
                "t_before" => TemporalOperator.Before,
                "t_contains" => TemporalOperator.Contains,
                "t_disjoint" => TemporalOperator.Disjoint,
                "t_during" => TemporalOperator.During,
                "t_equals" => TemporalOperator.Equals,
                "t_finishedby" => TemporalOperator.FinishedBy,
                "t_finishes" => TemporalOperator.Finishes,
                "t_intersects" => TemporalOperator.Intersects,
                "t_meets" => TemporalOperator.Meets,
                "t_metby" => TemporalOperator.MetBy,
                "t_overlappedby" => TemporalOperator.OverlappedBy,
                "t_overlaps" => TemporalOperator.Overlaps,
                "t_startedby" => TemporalOperator.StartedBy,
                "t_starts" => TemporalOperator.Starts,
                _ => throw new ArgumentException($"Unsupported temporal operator '{opValue}'")
            };

            return new TemporalPredicate(temporalOperator, expressions[0], expressions[1]);
        }

        if (normalizedLower.StartsWith("a_", StringComparison.Ordinal))
        {
            var expressions = ParseArguments(argsElement);
            if (expressions.Count != 2)
            {
                throw new ArgumentException($"Array operator '{opValue}' requires two arguments");
            }

            var arrayOperator = normalizedLower switch
            {
                "a_equals" => ArrayOperator.Equals,
                "a_contains" => ArrayOperator.Contains,
                "a_containedby" => ArrayOperator.ContainedBy,
                "a_overlaps" => ArrayOperator.Overlaps,
                _ => throw new ArgumentException($"Unsupported array operator '{opValue}'")
            };

            return new ArrayPredicate(arrayOperator, expressions[0], expressions[1]);
        }

        if (normalizedLower is "+" or "-" or "*" or "/" or "%" or "div" or "^")
        {
            var expressions = ParseArguments(argsElement);
            if (expressions.Count is < 1 or > 2)
            {
                throw new ArgumentException($"Arithmetic operator '{opValue}' requires one or two arguments");
            }

            if (expressions.Count == 1)
            {
                if (normalizedLower == "-")
                {
                    return new UnaryExpression(UnaryOperator.Negate, expressions[0]);
                }

                return expressions[0];
            }

            var op = normalizedLower switch
            {
                "+" => BinaryOperator.Add,
                "-" => BinaryOperator.Subtract,
                "*" => BinaryOperator.Multiply,
                "/" => BinaryOperator.Divide,
                "%" => BinaryOperator.Modulo,
                "div" => BinaryOperator.Div,
                "^" => BinaryOperator.Power,
                _ => BinaryOperator.Add
            };

            return new BinaryExpression(expressions[0], op, expressions[1]);
        }

        return new FunctionCall(normalized, ParseArguments(argsElement));
    }

    private List<FilterExpression> ParseArguments(JsonElement argsElement)
    {
        if (argsElement.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("CQL2-JSON args must be an array");
        }

        var expressions = new List<FilterExpression>();
        foreach (var arg in argsElement.EnumerateArray())
        {
            expressions.Add(ParseExpression(arg));
        }

        return expressions;
    }

    private ArrayLiteral ParseArrayLiteral(JsonElement element)
    {
        var values = new List<FilterExpression>();
        foreach (var item in element.EnumerateArray())
        {
            values.Add(ParseArrayElement(item));
        }

        return new ArrayLiteral(values);
    }

    private FilterExpression ParseArrayElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            return ParseArrayLiteral(element);
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryParseTemporalLiteral(element, out var temporalLiteral))
            {
                return temporalLiteral!;
            }

            if (TryParseIntervalLiteral(element, out var intervalLiteral))
            {
                return intervalLiteral!;
            }

            if (LooksLikeGeometry(element) || LooksLikeBbox(element))
            {
                return ParseGeometryLiteral(element);
            }

            if (element.TryGetProperty("property", out var propertyElement))
            {
                return new PropertyReference(propertyElement.GetString() ?? string.Empty);
            }

            if (element.TryGetProperty("op", out _))
            {
                return ParseObjectExpression(element);
            }
        }

        return ParseLiteral(element);
    }

    private static ValueList ExtractValueList(FilterExpression expression)
    {
        if (expression is ArrayLiteral arrayLiteral)
        {
            return new ValueList(arrayLiteral.Elements);
        }

        if (expression is Literal literal)
        {
            return new ValueList([literal]);
        }

        if (expression is ValueList valueList)
        {
            return valueList;
        }

        throw new ArgumentException("CQL2-JSON IN operator requires an array of scalar values");
    }

    private static Literal ParseLiteral(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => new Literal(element.GetString(), LiteralType.Text),
            JsonValueKind.Number => element.TryGetInt64(out var longValue)
                ? new Literal(longValue, LiteralType.Number)
                : new Literal(element.GetDouble(), LiteralType.Number),
            JsonValueKind.True => new Literal(true, LiteralType.Boolean),
            JsonValueKind.False => new Literal(false, LiteralType.Boolean),
            JsonValueKind.Null => new Literal(null, LiteralType.Null),
            _ => throw new ArgumentException($"Unsupported literal type: {element.ValueKind}")
        };
    }

    private static bool TryParseTemporalLiteral(JsonElement element, out Literal? literal)
    {
        literal = null;

        if (element.TryGetProperty("date", out var dateElement))
        {
            var dateValue = dateElement.GetString();
            if (string.IsNullOrWhiteSpace(dateValue))
            {
                throw new ArgumentException("CQL2-JSON date literal cannot be empty");
            }

            if (!DateOnly.TryParseExact(dateValue, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
            {
                throw new ArgumentException($"Invalid date literal: {dateValue}");
            }

            literal = new Literal(date, LiteralType.Date);
            return true;
        }

        if (element.TryGetProperty("timestamp", out var timestampElement))
        {
            var timestampValue = timestampElement.GetString();
            if (string.IsNullOrWhiteSpace(timestampValue))
            {
                throw new ArgumentException("CQL2-JSON timestamp literal cannot be empty");
            }

            if (!DateTimeOffset.TryParse(timestampValue, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
            {
                throw new ArgumentException($"Invalid timestamp literal: {timestampValue}");
            }

            literal = new Literal(timestamp, LiteralType.DateTime);
            return true;
        }

        return false;
    }

    private static bool TryParseIntervalLiteral(JsonElement element, out IntervalLiteral? literal)
    {
        literal = null;

        if (!element.TryGetProperty("interval", out var intervalElement))
        {
            return false;
        }

        if (intervalElement.ValueKind != JsonValueKind.Array || intervalElement.GetArrayLength() != 2)
        {
            throw new ArgumentException("CQL2-JSON interval must be an array with two elements");
        }

        var start = ParseIntervalBound(intervalElement[0]);
        var end = ParseIntervalBound(intervalElement[1]);

        if (start != null && end != null && start.Type != end.Type)
        {
            throw new ArgumentException("Interval bounds must share the same temporal granularity");
        }

        literal = new IntervalLiteral(start, end);
        return true;
    }

    private static Literal? ParseIntervalBound(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException("Interval bound cannot be empty");
            }

            if (text == "..")
            {
                return null;
            }

            if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
            {
                return new Literal(date, LiteralType.Date);
            }

            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
            {
                return new Literal(timestamp, LiteralType.DateTime);
            }

            throw new ArgumentException($"Invalid interval bound: {text}");
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryParseTemporalLiteral(element, out var temporalLiteral))
            {
                return temporalLiteral;
            }
        }

        throw new ArgumentException("Invalid interval bound value");
    }

    private static bool LooksLikeGeometry(JsonElement element)
    {
        return element.TryGetProperty("type", out _) && element.TryGetProperty("coordinates", out _) ||
               element.TryGetProperty("type", out _) && element.TryGetProperty("geometries", out _);
    }

    private static bool LooksLikeBbox(JsonElement element)
    {
        return element.TryGetProperty("bbox", out var bboxElement) &&
               bboxElement.ValueKind == JsonValueKind.Array &&
               (bboxElement.GetArrayLength() == 4 || bboxElement.GetArrayLength() == 6);
    }

    private static GeometryLiteral ParseGeometryLiteral(JsonElement element)
    {
        if (element.TryGetProperty("bbox", out _))
        {
            return ParseBboxLiteral(element);
        }

        var geoJson = element.GetRawText();

        try
        {
            var reader = new GeoJsonReader();
            var geometry = reader.Read<Geometry>(geoJson)
                ?? throw new ArgumentException("Geometry could not be parsed");

            var srid = ResolveGeometrySrid(element, geometry);
            var writer = new WKBWriter();
            var wkb = writer.Write(geometry);

            return new GeometryLiteral(wkb, srid, geoJson);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Invalid geometry literal in CQL2-JSON: {ex.Message}", ex);
        }
    }

    private static GeometryLiteral ParseBboxLiteral(JsonElement element)
    {
        if (!element.TryGetProperty("bbox", out var bboxElement) ||
            bboxElement.ValueKind != JsonValueKind.Array ||
            (bboxElement.GetArrayLength() != 4 && bboxElement.GetArrayLength() != 6))
        {
            throw new ArgumentException("Invalid bbox literal in CQL2-JSON");
        }

        var minX = bboxElement[0].GetDouble();
        var minY = bboxElement[1].GetDouble();
        var maxX = bboxElement[2].GetDouble();
        var maxY = bboxElement[3].GetDouble();
        var srid = TryExtractSridFromGeometryCrs(element, out var explicitSrid) ? explicitSrid : DefaultSrid;

        var coordinates = new[]
        {
            new Coordinate(minX, minY),
            new Coordinate(maxX, minY),
            new Coordinate(maxX, maxY),
            new Coordinate(minX, maxY),
            new Coordinate(minX, minY)
        };

        var geometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: srid);
        var polygon = geometryFactory.CreatePolygon(coordinates);
        var writer = new WKBWriter();
        var wkb = writer.Write(polygon);

        return new GeometryLiteral(wkb, srid, element.GetRawText());
    }

    private static int ResolveGeometrySrid(JsonElement geometryElement, Geometry geometry)
    {
        if (TryExtractSridFromGeometryCrs(geometryElement, out var explicitSrid))
        {
            return explicitSrid;
        }

        if (geometry.SRID > 0)
        {
            return geometry.SRID;
        }

        return DefaultSrid;
    }

    private static bool TryExtractSridFromGeometryCrs(JsonElement geometryElement, out int srid)
    {
        srid = 0;

        if (!geometryElement.TryGetProperty("crs", out var crsElement))
        {
            return false;
        }

        return TryExtractSridFromCrsElement(crsElement, out srid);
    }

    private static bool TryExtractSridFromCrsElement(JsonElement crsElement, out int srid)
    {
        srid = 0;

        if (crsElement.ValueKind == JsonValueKind.String)
        {
            return TryParseSrid(crsElement.GetString(), out srid);
        }

        if (crsElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (crsElement.TryGetProperty("properties", out var propertiesElement) &&
            propertiesElement.ValueKind == JsonValueKind.Object &&
            propertiesElement.TryGetProperty("name", out var nameElement) &&
            nameElement.ValueKind == JsonValueKind.String &&
            TryParseSrid(nameElement.GetString(), out srid))
        {
            return true;
        }

        return crsElement.TryGetProperty("name", out var directNameElement) &&
               directNameElement.ValueKind == JsonValueKind.String &&
               TryParseSrid(directNameElement.GetString(), out srid);
    }

    private static bool TryParseSrid(string? crsIdentifier, out int srid)
    {
        srid = 0;

        if (string.IsNullOrWhiteSpace(crsIdentifier))
        {
            return false;
        }

        var trimmed = crsIdentifier.Trim();
        if (trimmed.StartsWith("EPSG:", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(
                trimmed.AsSpan("EPSG:".Length),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out srid) && srid > 0;
        }

        ReadOnlySpan<char> span = trimmed.AsSpan();
        var trailingDigitsStart = span.Length;
        while (trailingDigitsStart > 0 && char.IsDigit(span[trailingDigitsStart - 1]))
        {
            trailingDigitsStart--;
        }

        if (trailingDigitsStart == span.Length)
        {
            return false;
        }

        var digits = span[trailingDigitsStart..];
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out srid) &&
               srid > 0;
    }
}

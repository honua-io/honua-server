// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.Shared.Models;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Core.Queries.Filters.Cql2;

/// <summary>
/// Parses CQL2-JSON filter expressions into the shared AST representation.
/// </summary>
public sealed class Cql2JsonParser
{
    // Per-parse recursion counter so that deeply nested CQL2-JSON documents
    // (`{"op":"not","args":[{"op":"not","args":[...]}]}` × thousands) cannot blow
    // the native call stack. Matches the MaxExpressionDepth guard the text parser
    // enforces via Cql2Parser.ParseWithDepth.
    private int _expressionDepth;

    private const int DefaultSrid = 4326;
    private const string Crs84Uri = "http://www.opengis.net/def/crs/OGC/1.3/CRS84";
    private static readonly Regex EpsgCodePattern = new(
        @"^EPSG:(?<code>[1-9]\d*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex EpsgUrnPattern = new(
        @"^urn:ogc:def:crs:EPSG::(?<code>[1-9]\d*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex EpsgUriPattern = new(
        @"^https?://www\.opengis\.net/def/crs/EPSG/0/(?<code>[1-9]\d*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Crs84UriPattern = new(
        @"^https?://www\.opengis\.net/def/crs/OGC/1\.3/CRS84$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Crs84UrnPattern = new(
        @"^urn:ogc:def:crs:OGC:(?:1\.3:|:)?CRS84$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

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
            _expressionDepth = 0;
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
        _expressionDepth++;
        FilterParserGuard.EnsureExpressionDepth(_expressionDepth);
        try
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
        finally
        {
            _expressionDepth--;
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
            var geometry = FilterParserGuard.ParseGeoJsonGeometry(geoJson, "CQL2-JSON geometry literal");
            var srid = ResolveGeometrySrid(element, geometry);
            geometry.SRID = srid;
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

        if (bboxElement.GetArrayLength() == 6)
        {
            throw new ArgumentException("3D bounding boxes are not supported.");
        }

        var minX = bboxElement[0].GetDouble();
        var minY = bboxElement[1].GetDouble();
        var maxX = bboxElement[2].GetDouble();
        var maxY = bboxElement[3].GetDouble();
        var srid = ResolveGeometrySrid(element) ?? DefaultSrid;
        var geometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: srid);
        var geometry = CreateBboxGeometry(geometryFactory, minX, minY, maxX, maxY, srid);
        var writer = new WKBWriter();
        var wkb = writer.Write(geometry);

        return new GeometryLiteral(wkb, srid, element.GetRawText());
    }

    private static Geometry CreateBboxGeometry(
        GeometryFactory geometryFactory,
        double minX,
        double minY,
        double maxX,
        double maxY,
        int srid)
    {
        var isGeographic = SpatialReference.Create(srid).IsGeographic;
        ValidateBbox(minX, minY, maxX, maxY, isGeographic);

        if (isGeographic && minX > maxX)
        {
            var eastHemisphere = CreateBboxPolygon(geometryFactory, minX, minY, 180.0, maxY);
            var westHemisphere = CreateBboxPolygon(geometryFactory, -180.0, minY, maxX, maxY);
            return geometryFactory.CreateMultiPolygon([eastHemisphere, westHemisphere]);
        }

        return geometryFactory.ToGeometry(new Envelope(minX, maxX, minY, maxY));
    }

    private static Polygon CreateBboxPolygon(
        GeometryFactory geometryFactory,
        double minX,
        double minY,
        double maxX,
        double maxY)
        => geometryFactory.CreatePolygon(
            [
                new Coordinate(minX, minY),
                new Coordinate(maxX, minY),
                new Coordinate(maxX, maxY),
                new Coordinate(minX, maxY),
                new Coordinate(minX, minY)
            ]);

    private static int ResolveGeometrySrid(JsonElement geometryElement, Geometry geometry)
    {
        var explicitSrid = ResolveGeometrySrid(geometryElement);
        if (explicitSrid.HasValue)
        {
            return explicitSrid.Value;
        }

        if (geometry.SRID > 0)
        {
            return geometry.SRID;
        }

        return DefaultSrid;
    }

    private static int? ResolveGeometrySrid(JsonElement geometryElement)
    {
        if (!geometryElement.TryGetProperty("crs", out var crsElement))
        {
            return null;
        }

        if (!TryExtractSridFromCrsElement(crsElement, out var srid))
        {
            throw new ArgumentException($"Invalid geometry CRS identifier: {crsElement.GetRawText()}");
        }

        return srid;
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

    private static void ValidateBbox(double minX, double minY, double maxX, double maxY, bool isGeographic)
    {
        if (!IsValidCoordinate(minX) || !IsValidCoordinate(minY) ||
            !IsValidCoordinate(maxX) || !IsValidCoordinate(maxY) ||
            minY >= maxY ||
            (!isGeographic && minX >= maxX) ||
            (isGeographic && minX == maxX))
        {
            throw new ArgumentException("Invalid bbox literal in CQL2-JSON");
        }

        if (isGeographic &&
            (!IsValidLongitude(minX) || !IsValidLongitude(maxX) ||
             !IsValidLatitude(minY) || !IsValidLatitude(maxY)))
        {
            throw new ArgumentException("Invalid bbox literal in CQL2-JSON");
        }
    }

    private static bool IsValidCoordinate(double coordinate)
        => !double.IsNaN(coordinate) &&
           !double.IsInfinity(coordinate) &&
           coordinate >= -40_000_000 &&
           coordinate <= 40_000_000;

    private static bool IsValidLongitude(double coordinate)
        => coordinate >= -180.0 && coordinate <= 180.0;

    private static bool IsValidLatitude(double coordinate)
        => coordinate >= -90.0 && coordinate <= 90.0;

    private static bool TryParseSrid(string? crsIdentifier, out int srid)
    {
        srid = 0;

        if (string.IsNullOrWhiteSpace(crsIdentifier))
        {
            return false;
        }

        var trimmed = TrimWrappers(crsIdentifier);

        if (IsCrs84Identifier(trimmed))
        {
            srid = DefaultSrid;
            return true;
        }

        return TryParsePositiveInt(trimmed, out srid) ||
               TryParseRegexSrid(trimmed, EpsgCodePattern, out srid) ||
               TryParseRegexSrid(trimmed, EpsgUrnPattern, out srid) ||
               TryParseRegexSrid(trimmed, EpsgUriPattern, out srid);
    }

    private static string TrimWrappers(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length > 1 &&
            ((trimmed[0] == '[' && trimmed[^1] == ']') ||
             (trimmed[0] == '<' && trimmed[^1] == '>')))
        {
            trimmed = trimmed[1..^1].Trim();
        }

        return trimmed;
    }

    private static bool IsCrs84Identifier(string value)
    {
        return value.Equals("CRS84", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("OGC:CRS84", StringComparison.OrdinalIgnoreCase) ||
               value.Equals(Crs84Uri, StringComparison.OrdinalIgnoreCase) ||
               Crs84UriPattern.IsMatch(value) ||
               Crs84UrnPattern.IsMatch(value);
    }

    private static bool TryParseRegexSrid(string value, Regex regex, out int srid)
    {
        srid = 0;
        var match = regex.Match(value);
        return match.Success && TryParsePositiveInt(match.Groups["code"].Value, out srid);
    }

    private static bool TryParsePositiveInt(string value, out int srid)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out srid) && srid > 0;
    }
}

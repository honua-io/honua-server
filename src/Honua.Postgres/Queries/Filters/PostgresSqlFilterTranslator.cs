// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Postgres.Features.FeatureStore;
using Honua.Postgres.Features.Infrastructure;

namespace Honua.Postgres.Queries.Filters;

/// <summary>
/// Translates filter expressions to parameterized PostgreSQL WHERE clauses
/// </summary>
internal sealed class PostgresSqlFilterTranslator : ISqlFilterTranslator
{
    /// <summary>
    /// Maximum allowed nesting depth for filter expressions.
    /// Prevents stack overflow from maliciously crafted deeply-nested filters.
    /// </summary>
    internal const int MaxExpressionDepth = FilterExpressionNormalizer.MaxExpressionDepth;

    private int _paramIndex;
    private int _depth;
    private readonly List<object?> _parameters = [];
    private readonly bool _useJsonAttributes;
    private readonly string _attributesColumn;
    private readonly string _geometryColumn;
    private readonly string _primaryKeyColumn;

    public PostgresSqlFilterTranslator(
        bool useJsonAttributes = false,
        string attributesColumn = DatabaseSchema.AttributesColumn,
        string geometryColumn = DatabaseSchema.GeometryColumn,
        string primaryKeyColumn = DatabaseSchema.ObjectIdColumn)
    {
        _useJsonAttributes = useJsonAttributes;
        _attributesColumn = attributesColumn;
        _geometryColumn = geometryColumn;
        _primaryKeyColumn = primaryKeyColumn;
    }

    /// <summary>
    /// Translates a filter expression to SQL using a Metadata v2 resource for
    /// field validation and spatial-reference resolution.
    /// </summary>
    /// <param name="filter">Filter expression to translate.</param>
    /// <param name="resource">Metadata v2 resource.</param>
    /// <returns>SQL fragment with parameters.</returns>
    public SqlFragment Translate(FilterExpression filter, MetadataV2Resource resource)
        => TranslateCore(filter, FilterTranslationContext.FromResource(resource));

    private SqlFragment TranslateCore(FilterExpression filter, FilterTranslationContext context)
    {
        _paramIndex = 0;
        _depth = 0;
        _parameters.Clear();

        var sql = TranslateExpression(filter, context);
        return new SqlFragment(sql, _parameters);
    }

    private string TranslateExpression(FilterExpression filter, FilterTranslationContext context)
    {
        try
        {
            if (++_depth > MaxExpressionDepth)
            {
                throw new ArgumentException(
                    $"Filter expression exceeds the maximum nesting depth of {MaxExpressionDepth}.");
            }

            return filter switch
            {
                BinaryExpression bin => TranslateBinary(bin, context),
                UnaryExpression un => TranslateUnary(un, context),
                PropertyReference prop => TranslateProperty(prop, context),
                Literal lit => TranslateLiteral(lit),
                SpatialPredicate spatial => TranslateSpatial(spatial, context),
                SpatialDistancePredicate spatialDistance => TranslateSpatialDistance(spatialDistance, context),
                TemporalPredicate temporal => TranslateTemporal(temporal, context),
                ArrayPredicate array => TranslateArrayPredicate(array, context),
                FunctionCall func => TranslateFunction(func, context),
                IntervalLiteral interval => TranslateIntervalLiteral(interval),
                ArrayLiteral arrayLiteral => TranslateArrayLiteral(arrayLiteral),
                ValueList list => TranslateValueList(list, context),
                _ => throw new NotSupportedException($"Unknown filter type: {filter.GetType()}")
            };
        }
        finally
        {
            _depth--;
        }
    }

    private string TranslateBinary(BinaryExpression binary, FilterTranslationContext context)
    {
        if (binary.Right is ValueList valueList && valueList.Values.Count == 0)
        {
            return binary.Operator switch
            {
                BinaryOperator.In => "FALSE",
                BinaryOperator.NotIn => "TRUE",
                _ => TranslateBinaryWithValues(binary, context)
            };
        }

        return TranslateBinaryWithValues(binary, context);
    }

    private string TranslateBinaryWithValues(BinaryExpression binary, FilterTranslationContext context)
    {
        var left = TranslateExpression(binary.Left, context);
        var right = TranslateExpression(binary.Right, context);

        var op = binary.Operator switch
        {
            BinaryOperator.And => "AND",
            BinaryOperator.Or => "OR",
            BinaryOperator.Equal => "=",
            BinaryOperator.NotEqual => "<>",
            BinaryOperator.LessThan => "<",
            BinaryOperator.LessThanOrEqual => "<=",
            BinaryOperator.GreaterThan => ">",
            BinaryOperator.GreaterThanOrEqual => ">=",
            BinaryOperator.Like => "LIKE",
            BinaryOperator.NotLike => "NOT LIKE",
            BinaryOperator.In => "IN",
            BinaryOperator.NotIn => "NOT IN",
            BinaryOperator.Add => "+",
            BinaryOperator.Subtract => "-",
            BinaryOperator.Multiply => "*",
            BinaryOperator.Divide => "/",
            BinaryOperator.Modulo => "%",
            BinaryOperator.Div => "/",
            BinaryOperator.Power => "^",
            _ => throw new NotSupportedException($"Binary operator {binary.Operator}")
        };

        // Handle logical operators with proper parentheses
        if (binary.Operator is BinaryOperator.And or BinaryOperator.Or)
        {
            return $"({left} {op} {right})";
        }

        if (binary.Operator == BinaryOperator.Div)
        {
            return $"TRUNC(({left}) / ({right}))";
        }

        if (binary.Operator == BinaryOperator.Power)
        {
            return $"POWER({left}, {right})";
        }

        if (binary.Operator is BinaryOperator.Add or BinaryOperator.Subtract or
            BinaryOperator.Multiply or BinaryOperator.Divide or BinaryOperator.Modulo)
        {
            return $"({left} {op} {right})";
        }

        return $"{left} {op} {right}";
    }

    private string TranslateUnary(UnaryExpression unary, FilterTranslationContext context)
    {
        var operand = TranslateExpression(unary.Operand, context);

        return unary.Operator switch
        {
            UnaryOperator.Not => $"NOT ({operand})",
            UnaryOperator.IsNull => $"{operand} IS NULL",
            UnaryOperator.IsNotNull => $"{operand} IS NOT NULL",
            UnaryOperator.Negate => $"-({operand})",
            _ => throw new NotSupportedException($"Unary operator {unary.Operator}")
        };
    }

    private string TranslateProperty(PropertyReference property, FilterTranslationContext context)
    {
        if (_useJsonAttributes && TryMapCoreField(property.PropertyName, context, out var coreExpression))
        {
            return coreExpression;
        }

        // Validate field exists in the resource schema
        var field = context.TryGetField(property.PropertyName) ??
            throw new ArgumentException(ErrorMessages.NotFound.FormatField(property.PropertyName, context.ResourceName));

        if (!_useJsonAttributes)
        {
            // Quote field name to handle reserved words and mixed-case names
            // PostgreSQL uses double quotes for identifiers
            return QuoteIdentifier(field.Name);
        }

        if (field.IsGeometry)
        {
            return GetGeometryColumnExpression(context);
        }

        if (field.IsPrimaryKey)
        {
            return QuoteIdentifier(_primaryKeyColumn);
        }

        var attributesColumn = QuoteIdentifier(_attributesColumn);
        var key = EscapeSqlLiteral(field.Name);
        var baseExpression = $"{attributesColumn} ->> '{key}'";
        var castType = GetJsonCastType(field.Type);

        if (castType == null)
        {
            return baseExpression;
        }

        var nullSafe = $"NULLIF({baseExpression}, '')";
        return $"{nullSafe}::{castType}";
    }

    private string TranslateLiteral(Literal literal)
    {
        var paramName = $"@p{_paramIndex++}";
        _parameters.Add(literal.Value);
        return paramName;
    }

    private string TranslateSpatial(SpatialPredicate spatial, FilterTranslationContext context)
    {
        var left = TranslateGeometryExpression(spatial.Left, context);
        var right = TranslateGeometryExpression(spatial.Right, context);
        var function = spatial.Operator switch
        {
            SpatialOperator.Intersects => "ST_Intersects",
            SpatialOperator.Contains => "ST_Contains",
            SpatialOperator.Within => "ST_Within",
            SpatialOperator.Crosses => "ST_Crosses",
            SpatialOperator.Touches => "ST_Touches",
            SpatialOperator.Overlaps => "ST_Overlaps",
            SpatialOperator.Disjoint => "ST_Disjoint",
            SpatialOperator.Equals => "ST_Equals",
            _ => throw new NotSupportedException($"Spatial operator {spatial.Operator}")
        };

        return $"{function}({left}, {right})";
    }

    private string TranslateSpatialDistance(SpatialDistancePredicate spatial, FilterTranslationContext context)
    {
        var useGeography = IsGeographicContext(context);
        var left = useGeography
            ? TranslateGeographyExpression(spatial.Left, context)
            : TranslateGeometryExpression(spatial.Left, context);
        var right = useGeography
            ? TranslateGeographyExpression(spatial.Right, context)
            : TranslateGeometryExpression(spatial.Right, context);
        var distance = TranslateExpression(spatial.Distance, context);

        return spatial.Operator switch
        {
            SpatialOperator.DWithin => $"ST_DWithin({left}, {right}, {distance})",
            SpatialOperator.Beyond => $"ST_Distance({left}, {right}) > {distance}",
            _ => throw new NotSupportedException($"Spatial operator {spatial.Operator}")
        };
    }

    private static bool IsGeographicContext(FilterTranslationContext context)
    {
        if (context.IsGeographic)
        {
            return true;
        }

        return IsLikelyGeographicSrid(context.Wkid);
    }

    private static bool IsLikelyGeographicSrid(int srid)
        => srid == SpatialReference.WGS84.Wkid ||
           srid == 4269 ||
           srid == 4267 ||
           srid is >= 4000 and <= 4999;

    private string TranslateGeometryExpression(FilterExpression expression, FilterTranslationContext context)
    {
        switch (expression)
        {
            case GeometryLiteral geometry:
                {
                    var wkbParam = $"@p{_paramIndex++}";
                    var sridParam = $"@p{_paramIndex++}";
                    _parameters.Add(geometry.Wkb);
                    _parameters.Add(geometry.Srid);

                    var geometryExpression = $"ST_GeomFromWKB({wkbParam}, {sridParam})";
                    if (geometry.Srid != context.Wkid)
                    {
                        geometryExpression = $"ST_Transform({geometryExpression}, {context.Wkid})";
                    }

                    return geometryExpression;
                }
            case PropertyReference property:
                {
                    var field = context.TryGetField(property.PropertyName);
                    if (field is null)
                    {
                        if (IsGeometryAlias(property.PropertyName))
                        {
                            return GetGeometryColumnExpression(context);
                        }

                        throw new ArgumentException($"Field '{property.PropertyName}' is not a geometry field");
                    }

                    if (!field.Value.IsGeometry)
                    {
                        throw new ArgumentException($"Field '{property.PropertyName}' is not a geometry field");
                    }

                    return GetGeometryColumnExpression(context);
                }
            case FunctionCall functionCall:
                return TranslateFunction(functionCall, context);
            default:
                throw new NotSupportedException($"Unsupported geometry expression: {expression.GetType()}");
        }
    }

    private string TranslateTemporal(TemporalPredicate temporal, FilterTranslationContext context)
    {
        if (temporal.Operator == TemporalOperator.Before)
        {
            return TranslateTemporalBoundaryComparison(
                temporal.Left,
                temporal.Right,
                context,
                TemporalBoundary.End,
                TemporalBoundary.Start,
                "<");
        }

        if (temporal.Operator == TemporalOperator.After)
        {
            return TranslateTemporalBoundaryComparison(
                temporal.Left,
                temporal.Right,
                context,
                TemporalBoundary.Start,
                TemporalBoundary.End,
                ">");
        }

        if (temporal.Operator == TemporalOperator.Meets)
        {
            return TranslateTemporalBoundaryComparison(
                temporal.Left,
                temporal.Right,
                context,
                TemporalBoundary.End,
                TemporalBoundary.Start,
                "=");
        }

        if (temporal.Operator == TemporalOperator.MetBy)
        {
            return TranslateTemporalBoundaryComparison(
                temporal.Left,
                temporal.Right,
                context,
                TemporalBoundary.Start,
                TemporalBoundary.End,
                "=");
        }

        var left = TranslateTemporalExpression(temporal.Left, context);
        var right = TranslateTemporalExpression(temporal.Right, context);

        EnsureTemporalCompatibility(temporal.Operator, left, right);

        var leftStart = NormalizeTemporalStart(left);
        var leftEnd = NormalizeTemporalEnd(left);
        var rightStart = NormalizeTemporalStart(right);
        var rightEnd = NormalizeTemporalEnd(right);

        if (left.Kind != right.Kind)
        {
            leftStart = CastTemporal(leftStart, left.Kind, TemporalKind.Timestamp);
            leftEnd = CastTemporal(leftEnd, left.Kind, TemporalKind.Timestamp);
            rightStart = CastTemporal(rightStart, right.Kind, TemporalKind.Timestamp);
            rightEnd = CastTemporal(rightEnd, right.Kind, TemporalKind.Timestamp);
        }

        return temporal.Operator switch
        {
            TemporalOperator.Equals => $"({leftStart} = {rightStart} AND {leftEnd} = {rightEnd})",
            TemporalOperator.Disjoint => $"({leftEnd} < {rightStart} OR {leftStart} > {rightEnd})",
            TemporalOperator.Intersects => $"NOT ({leftEnd} < {rightStart} OR {leftStart} > {rightEnd})",
            TemporalOperator.Contains => $"({leftStart} < {rightStart} AND {leftEnd} > {rightEnd})",
            TemporalOperator.During => $"({leftStart} > {rightStart} AND {leftEnd} < {rightEnd})",
            TemporalOperator.Starts => $"({leftStart} = {rightStart} AND {leftEnd} < {rightEnd})",
            TemporalOperator.StartedBy => $"({leftStart} = {rightStart} AND {leftEnd} > {rightEnd})",
            TemporalOperator.Finishes => $"({leftEnd} = {rightEnd} AND {leftStart} > {rightStart})",
            TemporalOperator.FinishedBy => $"({leftEnd} = {rightEnd} AND {leftStart} < {rightStart})",
            TemporalOperator.Overlaps => $"({leftStart} < {rightStart} AND {leftEnd} > {rightStart} AND {leftEnd} < {rightEnd})",
            TemporalOperator.OverlappedBy => $"({rightStart} < {leftStart} AND {rightEnd} > {leftStart} AND {rightEnd} < {leftEnd})",
            _ => throw new NotSupportedException($"Temporal operator {temporal.Operator}")
        };
    }

    private string TranslateTemporalBoundaryComparison(
        FilterExpression leftExpression,
        FilterExpression rightExpression,
        FilterTranslationContext context,
        TemporalBoundary leftBoundary,
        TemporalBoundary rightBoundary,
        string sqlOperator)
    {
        var left = TranslateTemporalBoundary(leftExpression, context, leftBoundary);
        var right = TranslateTemporalBoundary(rightExpression, context, rightBoundary);

        var leftSql = left.Sql;
        var rightSql = right.Sql;
        if (left.Kind != right.Kind)
        {
            leftSql = CastTemporal(leftSql, left.Kind, TemporalKind.Timestamp);
            rightSql = CastTemporal(rightSql, right.Kind, TemporalKind.Timestamp);
        }

        return $"{leftSql} {sqlOperator} {rightSql}";
    }

    private string TranslateArrayPredicate(ArrayPredicate array, FilterTranslationContext context)
    {
        var left = TranslateArrayExpression(array.Left, context);
        var right = TranslateArrayExpression(array.Right, context);

        return array.Operator switch
        {
            ArrayOperator.Equals => $"{left} = {right}",
            ArrayOperator.Contains => $"{left} @> {right}",
            ArrayOperator.ContainedBy => $"{right} @> {left}",
            ArrayOperator.Overlaps => $"EXISTS (SELECT 1 FROM jsonb_array_elements({left}) AS l JOIN jsonb_array_elements({right}) AS r ON l.value = r.value)",
            _ => throw new NotSupportedException($"Array operator {array.Operator}")
        };
    }

    private string TranslateFunction(FunctionCall function, FilterTranslationContext context)
    {
        if (string.Equals(function.FunctionName, "GEODISTANCE", StringComparison.OrdinalIgnoreCase))
        {
            return TranslateGeoDistance(function, context);
        }

        var args = function.Arguments.Select(arg => TranslateExpression(arg, context)).ToArray();
        var argString = string.Join(", ", args);

        return function.FunctionName.ToUpperInvariant() switch
        {
            "UPPER" => $"UPPER({argString})",
            "LOWER" => $"LOWER({argString})",
            "LENGTH" => $"LENGTH({argString})",
            "CHAR_LENGTH" => $"CHAR_LENGTH({argString})",
            "CHARACTER_LENGTH" => $"CHARACTER_LENGTH({argString})",
            "TRIM" => $"TRIM({argString})",
            "LTRIM" => $"LTRIM({argString})",
            "RTRIM" => $"RTRIM({argString})",
            "SUBSTRING" => TranslateSubstring(args, function.FunctionName),
            "SUBSTR" => TranslateSubstring(args, function.FunctionName),
            "REPLACE" => $"REPLACE({argString})",
            "CONCAT" => $"CONCAT({argString})",
            "POSITION" => args.Length == 2
                ? $"POSITION({args[0]} IN {args[1]})"
                : throw new ArgumentException("POSITION requires two arguments"),
            "ABS" => $"ABS({argString})",
            "CEIL" => $"CEIL({argString})",
            "CEILING" => $"CEILING({argString})",
            "FLOOR" => $"FLOOR({argString})",
            "ROUND" => $"ROUND({argString})",
            "NOW" => args.Length == 0
                ? "NOW()"
                : throw new ArgumentException("NOW does not accept arguments"),
            "YEAR" => args.Length == 1
                ? $"EXTRACT(YEAR FROM {args[0]})"
                : throw new ArgumentException("YEAR requires one argument"),
            "MONTH" => args.Length == 1
                ? $"EXTRACT(MONTH FROM {args[0]})"
                : throw new ArgumentException("MONTH requires one argument"),
            "DAY" => args.Length == 1
                ? $"EXTRACT(DAY FROM {args[0]})"
                : throw new ArgumentException("DAY requires one argument"),
            "HOUR" => args.Length == 1
                ? $"EXTRACT(HOUR FROM {args[0]})"
                : throw new ArgumentException("HOUR requires one argument"),
            "MINUTE" => args.Length == 1
                ? $"EXTRACT(MINUTE FROM {args[0]})"
                : throw new ArgumentException("MINUTE requires one argument"),
            "SECOND" => args.Length == 1
                ? $"EXTRACT(SECOND FROM {args[0]})"
                : throw new ArgumentException("SECOND requires one argument"),
            "CURRENT_DATE" => args.Length == 0
                ? "CURRENT_DATE"
                : throw new ArgumentException("CURRENT_DATE does not accept arguments"),
            "CURRENT_TIMESTAMP" => args.Length == 0
                ? "CURRENT_TIMESTAMP"
                : throw new ArgumentException("CURRENT_TIMESTAMP does not accept arguments"),
            "CURRENT_TIME" => args.Length == 0
                ? "CURRENT_TIME"
                : throw new ArgumentException("CURRENT_TIME does not accept arguments"),
            "COALESCE" => $"COALESCE({argString})",
            "NULLIF" => $"NULLIF({argString})",
            "POWER" => $"POWER({argString})",
            "MOD" => args.Length == 2
                ? $"MOD({CastNumeric(args[0])}, {CastNumeric(args[1])})"
                : throw new ArgumentException("MOD requires two arguments"),
            "CASEI" => $"LOWER({argString})",
            "ACCENTI" => $"UNACCENT(LOWER({argString}))",

            // Spatial functions per OGC standards
            "ST_AREA" => TranslateSpatialFunction("ST_Area", args, 1),
            "ST_LENGTH" => TranslateSpatialFunction("ST_Length", args, 1),
            "ST_PERIMETER" => TranslateSpatialFunction("ST_Perimeter", args, 1),
            "ST_DISTANCE" => TranslateSpatialFunction("ST_Distance", args, 2),
            "ST_CENTROID" => TranslateSpatialFunction("ST_Centroid", args, 1),
            "ST_BUFFER" => TranslateSpatialFunction("ST_Buffer", args, 2),
            "ST_ENVELOPE" => TranslateSpatialFunction("ST_Envelope", args, 1),
            "ST_CONVEXHULL" => TranslateSpatialFunction("ST_ConvexHull", args, 1),
            "ST_BOUNDARY" => TranslateSpatialFunction("ST_Boundary", args, 1),
            "ST_NUMGEOMETRIES" => TranslateSpatialFunction("ST_NumGeometries", args, 1),
            "ST_GEOMETRYTYPE" => TranslateSpatialFunction("ST_GeometryType", args, 1),
            "ST_SRID" => TranslateSpatialFunction("ST_SRID", args, 1),
            "ST_ISVALID" => TranslateSpatialFunction("ST_IsValid", args, 1),
            "ST_ISSIMPLE" => TranslateSpatialFunction("ST_IsSimple", args, 1),
            "ST_ISCLOSED" => TranslateSpatialFunction("ST_IsClosed", args, 1),
            "ST_ISEMPTY" => TranslateSpatialFunction("ST_IsEmpty", args, 1),

            // Math functions per OGC standards
            "SQRT" => $"SQRT({argString})",
            "SIN" => $"SIN({argString})",
            "COS" => $"COS({argString})",
            "TAN" => $"TAN({argString})",
            "LOG" => $"LN({argString})",
            "EXP" => $"EXP({argString})",

            // Aggregate functions
            "COUNT" => $"COUNT({argString})",
            "SUM" => $"SUM({argString})",
            "AVG" => $"AVG({argString})",
            "MIN" => $"MIN({argString})",
            "MAX" => $"MAX({argString})",

            // Type conversion
            "CAST" => TranslateCast(args, function.FunctionName),

            _ => throw new NotSupportedException($"Function {function.FunctionName}")
        };
    }

    private static string TranslateSubstring(string[] args, string functionName)
    {
        if (args.Length is < 2 or > 3)
        {
            throw new ArgumentException($"{functionName} requires 2 or 3 arguments.");
        }

        var start = CastInteger(args[1]);
        if (args.Length == 2)
        {
            return $"SUBSTRING({args[0]}, {start})";
        }

        var length = CastInteger(args[2]);
        return $"SUBSTRING({args[0]}, {start}, {length})";
    }

    private static string TranslateSpatialFunction(string postgisName, string[] args, int expectedArgCount)
    {
        if (args.Length != expectedArgCount)
        {
            throw new ArgumentException($"{postgisName} requires {expectedArgCount} argument(s)");
        }

        return $"{postgisName}({string.Join(", ", args)})";
    }

    private static string TranslateCast(string[] args, string functionName)
    {
        if (args.Length != 2)
        {
            throw new ArgumentException($"{functionName} requires two arguments (value, type)");
        }

        var value = args[0];
        var targetType = args[1].Trim('\'', '"').ToUpperInvariant();

        return targetType switch
        {
            "INTEGER" or "INT" => $"({value})::INTEGER",
            "NUMERIC" or "DECIMAL" => $"({value})::NUMERIC",
            "REAL" or "FLOAT" => $"({value})::REAL",
            "DOUBLE" or "DOUBLE PRECISION" => $"({value})::DOUBLE PRECISION",
            "TEXT" or "STRING" or "VARCHAR" => $"({value})::TEXT",
            "BOOLEAN" or "BOOL" => $"({value})::BOOLEAN",
            "DATE" => $"({value})::DATE",
            "TIMESTAMP" => $"({value})::TIMESTAMP",
            "GEOMETRY" => $"({value})::GEOMETRY",
            "GEOGRAPHY" => $"({value})::GEOGRAPHY",
            _ => throw new NotSupportedException($"Unsupported cast target type: {targetType}")
        };
    }

    private string TranslateGeoDistance(FunctionCall function, FilterTranslationContext context)
    {
        if (function.Arguments.Count != 2)
        {
            throw new ArgumentException("GEODISTANCE requires two arguments");
        }

        var left = TranslateGeographyExpression(function.Arguments[0], context);
        var right = TranslateGeographyExpression(function.Arguments[1], context);
        return $"ST_Distance({left}, {right})";
    }

    private string TranslateGeographyExpression(FilterExpression expression, FilterTranslationContext context)
    {
        switch (expression)
        {
            case GeometryLiteral geometry:
                {
                    var wkbParam = $"@p{_paramIndex++}";
                    var sridParam = $"@p{_paramIndex++}";
                    _parameters.Add(geometry.Wkb);
                    _parameters.Add(geometry.Srid);

                    var geometryExpression = $"ST_GeomFromWKB({wkbParam}, {sridParam})";
                    var wgs84Srid = SpatialReference.WGS84.Wkid;
                    if (geometry.Srid != wgs84Srid)
                    {
                        geometryExpression = $"ST_Transform({geometryExpression}, {wgs84Srid})";
                    }

                    return $"{geometryExpression}::geography";
                }
            case PropertyReference property:
                {
                    var field = context.TryGetField(property.PropertyName);
                    if (field is null && !IsGeometryAlias(property.PropertyName))
                    {
                        throw new ArgumentException($"Field '{property.PropertyName}' is not a geometry field");
                    }

                    if (field is not null && !field.Value.IsGeometry)
                    {
                        throw new ArgumentException($"Field '{property.PropertyName}' is not a geometry field");
                    }

                    var geometryExpression = GetGeometryColumnExpression(context);
                    var wgs84Srid = SpatialReference.WGS84.Wkid;
                    if (context.Wkid != wgs84Srid)
                    {
                        geometryExpression = $"ST_Transform({geometryExpression}, {wgs84Srid})";
                    }

                    return $"{geometryExpression}::geography";
                }
            default:
                throw new NotSupportedException($"Unsupported geography expression: {expression.GetType()}");
        }
    }

    private string TranslateIntervalLiteral(IntervalLiteral interval)
    {
        var boundsKind = interval.Start?.Type ?? interval.End?.Type ?? LiteralType.DateTime;
        var rangeFunction = boundsKind == LiteralType.Date ? "DATERANGE" : "TSTZRANGE";

        var startSql = interval.Start == null ? "NULL" : TranslateLiteral(interval.Start);
        var endSql = interval.End == null ? "NULL" : TranslateLiteral(interval.End);

        return $"{rangeFunction}({startSql}, {endSql}, '[]')";
    }

    private string TranslateArrayLiteral(ArrayLiteral arrayLiteral)
    {
        var values = arrayLiteral.Elements.Select(ConvertArrayElement).ToList();
        var json = JsonSerializer.Serialize(values, FeatureAttributesJsonContext.Default.ListObject);
        var paramName = $"@p{_paramIndex++}";
        _parameters.Add(json);
        return $"{paramName}::jsonb";
    }

    private object? ConvertArrayElement(FilterExpression expression)
    {
        return expression switch
        {
            Literal literal => ConvertLiteralValue(literal),
            ArrayLiteral array => array.Elements.Select(ConvertArrayElement).ToList(),
            IntervalLiteral interval => new Dictionary<string, object?>
            {
                ["interval"] = new object?[]
                {
                    ConvertIntervalBound(interval.Start),
                    ConvertIntervalBound(interval.End)
                }
            },
            GeometryLiteral geometry => ConvertGeometryLiteral(geometry),
            _ => throw new NotSupportedException($"Array literal element '{expression.GetType()}' is not supported")
        };
    }

    private static object? ConvertIntervalBound(Literal? literal)
    {
        if (literal == null)
        {
            return "..";
        }

        return literal.Type switch
        {
            LiteralType.Date => ((DateOnly)literal.Value!).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            LiteralType.DateTime => ((DateTimeOffset)literal.Value!).ToString("O", CultureInfo.InvariantCulture),
            _ => literal.Value
        };
    }

    private static object? ConvertLiteralValue(Literal literal)
    {
        return literal.Type switch
        {
            LiteralType.Date => new Dictionary<string, object?>
            {
                ["date"] = ((DateOnly)literal.Value!).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            },
            LiteralType.DateTime => new Dictionary<string, object?>
            {
                ["timestamp"] = ((DateTimeOffset)literal.Value!).ToString("O", CultureInfo.InvariantCulture)
            },
            _ => literal.Value
        };
    }

    private static object? ConvertGeometryLiteral(GeometryLiteral geometry)
    {
        var trimmed = geometry.OriginalFormat.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            using var document = JsonDocument.Parse(geometry.OriginalFormat);
            return document.RootElement.Clone();
        }

        return geometry.OriginalFormat;
    }

    private string TranslateValueList(ValueList valueList, FilterTranslationContext context)
    {
        var values = valueList.Values.Select(v => TranslateExpression(v, context));
        return $"({string.Join(", ", values)})";
    }

    private static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static string EscapeSqlLiteral(string value)
        => value.Replace("'", "''");

    private static string CastNumeric(string sql)
        => $"({sql})::numeric";

    private static string CastInteger(string sql)
        => $"({sql})::int";

    private string GetGeometryColumnExpression(FilterTranslationContext context)
    {
        var geomColumnName = _useJsonAttributes ? _geometryColumn : context.GeometryColumnName ?? _geometryColumn;
        var quoted = QuoteIdentifier(geomColumnName);
        return $"{quoted}::geometry";
    }

    private bool TryMapCoreField(string propertyName, FilterTranslationContext context, out string expression)
    {
        if (propertyName.Equals("id", StringComparison.OrdinalIgnoreCase) &&
            context.PrimaryKeyName is not null)
        {
            expression = QuoteIdentifier(_primaryKeyColumn);
            return true;
        }

        if (propertyName.Equals(DatabaseSchema.ObjectIdColumn, StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals(DatabaseSchema.ObjectIdColumnAlt, StringComparison.OrdinalIgnoreCase))
        {
            expression = QuoteIdentifier(_primaryKeyColumn);
            return true;
        }

        if (propertyName.Equals(DatabaseSchema.LayerIdColumnAlt, StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals(DatabaseSchema.LayerIdColumn, StringComparison.OrdinalIgnoreCase))
        {
            expression = QuoteIdentifier(DatabaseSchema.LayerIdColumn);
            return true;
        }

        if (IsGeometryAlias(propertyName))
        {
            expression = GetGeometryColumnExpression(context);
            return true;
        }

        if (propertyName.Equals("created_at", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("updated_at", StringComparison.OrdinalIgnoreCase))
        {
            expression = QuoteIdentifier(propertyName.ToLowerInvariant());
            return true;
        }

        expression = string.Empty;
        return false;
    }

    private static bool IsGeometryAlias(string propertyName)
        => propertyName.Equals(DatabaseSchema.GeometryColumn, StringComparison.OrdinalIgnoreCase) ||
           propertyName.Equals("shape", StringComparison.OrdinalIgnoreCase) ||
           propertyName.Equals("geometry", StringComparison.OrdinalIgnoreCase);

    private string TranslateArrayExpression(FilterExpression expression, FilterTranslationContext context)
    {
        if (expression is ArrayLiteral arrayLiteral)
        {
            return TranslateArrayLiteral(arrayLiteral);
        }

        if (expression is PropertyReference propertyReference)
        {
            var field = context.TryGetField(propertyReference.PropertyName);
            if (field is null || field.Value.Type != MetadataV2FieldType.Json)
            {
                throw new ArgumentException($"Array predicates require JSON array fields. '{propertyReference.PropertyName}' is not JSON.");
            }

            var attributesColumn = QuoteIdentifier(_attributesColumn);
            var key = EscapeSqlLiteral(field.Value.Name);
            return $"{attributesColumn} -> '{key}'";
        }

        throw new NotSupportedException($"Unsupported array expression: {expression.GetType()}");
    }

    private TemporalBounds TranslateTemporalExpression(FilterExpression expression, FilterTranslationContext context)
    {
        switch (expression)
        {
            case IntervalLiteral interval:
                {
                    var kind = interval.Start?.Type == LiteralType.Date || interval.End?.Type == LiteralType.Date
                        ? TemporalKind.Date
                        : TemporalKind.Timestamp;

                    var startSql = interval.Start == null ? "NULL" : TranslateLiteral(interval.Start);
                    var endSql = interval.End == null ? "NULL" : TranslateLiteral(interval.End);

                    return new TemporalBounds(
                        startSql,
                        endSql,
                        kind,
                        true,
                        interval.Start == null,
                        interval.End == null);
                }
            case Literal literal when literal.Type is LiteralType.Date or LiteralType.DateTime:
                {
                    var sql = TranslateLiteral(literal);
                    var kind = literal.Type == LiteralType.Date ? TemporalKind.Date : TemporalKind.Timestamp;
                    return new TemporalBounds(sql, sql, kind, false, false, false);
                }
            case PropertyReference property:
                {
                    var field = context.TryGetField(property.PropertyName);
                    if (field?.Type == MetadataV2FieldType.Time)
                    {
                        throw new ArgumentException(
                            $"Field '{property.PropertyName}' is a time-only field and cannot be used with temporal interval predicates.");
                    }

                    var kind = field?.Type == MetadataV2FieldType.Date ? TemporalKind.Date : TemporalKind.Timestamp;
                    var sql = TranslateProperty(property, context);
                    return new TemporalBounds(sql, sql, kind, false, false, false);
                }
            case FunctionCall functionCall:
                {
                    var sql = TranslateFunction(functionCall, context);
                    return new TemporalBounds(sql, sql, TemporalKind.Timestamp, false, false, false);
                }
            default:
                throw new NotSupportedException($"Unsupported temporal expression: {expression.GetType()}");
        }
    }

    private TemporalBound TranslateTemporalBoundary(
        FilterExpression expression,
        FilterTranslationContext context,
        TemporalBoundary boundary)
    {
        if (expression is IntervalLiteral interval)
        {
            var kind = interval.Start?.Type == LiteralType.Date || interval.End?.Type == LiteralType.Date
                ? TemporalKind.Date
                : TemporalKind.Timestamp;
            var literal = boundary == TemporalBoundary.Start ? interval.Start : interval.End;
            var sql = literal == null
                ? CreateOpenTemporalBoundarySql(kind, boundary)
                : TranslateLiteral(literal);

            return new TemporalBound(sql, kind);
        }

        var bounds = TranslateTemporalExpression(expression, context);
        return new TemporalBound(
            boundary == TemporalBoundary.Start
                ? NormalizeTemporalStart(bounds)
                : NormalizeTemporalEnd(bounds),
            bounds.Kind);
    }

    private static string CreateOpenTemporalBoundarySql(TemporalKind kind, TemporalBoundary boundary)
        => kind == TemporalKind.Date
            ? boundary == TemporalBoundary.Start ? "'-infinity'::date" : "'infinity'::date"
            : boundary == TemporalBoundary.Start ? "'-infinity'::timestamptz" : "'infinity'::timestamptz";

    private static void EnsureTemporalCompatibility(TemporalOperator op, TemporalBounds left, TemporalBounds right)
    {
        if (op is TemporalOperator.Contains or TemporalOperator.During or TemporalOperator.FinishedBy or
            TemporalOperator.Finishes or TemporalOperator.Meets or TemporalOperator.MetBy or
            TemporalOperator.OverlappedBy or TemporalOperator.Overlaps or TemporalOperator.StartedBy or
            TemporalOperator.Starts)
        {
            if (!left.IsInterval && !right.IsInterval)
            {
                throw new ArgumentException($"Temporal operator {op} requires interval operands");
            }
        }
    }

    private static string NormalizeTemporalStart(TemporalBounds bounds)
    {
        if (!bounds.IsInterval)
        {
            return bounds.StartSql;
        }

        if (!bounds.OpenStart)
        {
            return bounds.StartSql;
        }

        return bounds.Kind == TemporalKind.Date ? "'-infinity'::date" : "'-infinity'::timestamptz";
    }

    private static string NormalizeTemporalEnd(TemporalBounds bounds)
    {
        if (!bounds.IsInterval)
        {
            return bounds.EndSql;
        }

        if (!bounds.OpenEnd)
        {
            return bounds.EndSql;
        }

        return bounds.Kind == TemporalKind.Date ? "'infinity'::date" : "'infinity'::timestamptz";
    }

    private static string CastTemporal(string sql, TemporalKind from, TemporalKind to)
    {
        if (from == to)
        {
            return sql;
        }

        return to == TemporalKind.Timestamp ? $"({sql})::timestamptz" : $"({sql})::date";
    }

    private sealed record TemporalBounds(
        string StartSql,
        string EndSql,
        TemporalKind Kind,
        bool IsInterval,
        bool OpenStart,
        bool OpenEnd);

    private sealed record TemporalBound(
        string Sql,
        TemporalKind Kind);

    private enum TemporalBoundary
    {
        Start,
        End
    }

    private enum TemporalKind
    {
        Date,
        Timestamp
    }

    private static string? GetJsonCastType(MetadataV2FieldType fieldType)
    {
        return fieldType switch
        {
            MetadataV2FieldType.Integer => "integer",
            MetadataV2FieldType.BigInteger => "bigint",
            MetadataV2FieldType.Float => "real",
            MetadataV2FieldType.Double => "double precision",
            MetadataV2FieldType.Boolean => "boolean",
            MetadataV2FieldType.DateTime => "timestamptz",
            MetadataV2FieldType.Date => "date",
            MetadataV2FieldType.Time => "time",
            MetadataV2FieldType.Uuid => "uuid",
            _ => null
        };
    }
}

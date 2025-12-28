// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Queries.Filters;
using Honua.Postgres.Features.FeatureStore;

namespace Honua.Postgres.Queries.Filters;

/// <summary>
/// Translates filter expressions to parameterized PostgreSQL WHERE clauses
/// </summary>
internal sealed class PostgresSqlFilterTranslator : ISqlFilterTranslator
{
    private int _paramIndex;
    private readonly List<object?> _parameters = [];
    private readonly bool _useJsonAttributes;
    private readonly string _attributesColumn;
    private readonly string _geometryColumn;
    private readonly string _primaryKeyColumn;

    public PostgresSqlFilterTranslator(
        bool useJsonAttributes = false,
        string attributesColumn = "attributes",
        string geometryColumn = "geometry",
        string primaryKeyColumn = "objectid")
    {
        _useJsonAttributes = useJsonAttributes;
        _attributesColumn = attributesColumn;
        _geometryColumn = geometryColumn;
        _primaryKeyColumn = primaryKeyColumn;
    }

    /// <summary>
    /// Translates a filter expression to SQL
    /// </summary>
    /// <param name="filter">Filter expression to translate</param>
    /// <param name="layer">Layer definition for field validation</param>
    /// <returns>SQL fragment with parameters</returns>
    public SqlFragment Translate(FilterExpression filter, LayerDefinition layer)
    {
        _paramIndex = 0;
        _parameters.Clear();

        var sql = TranslateExpression(filter, layer);
        return new SqlFragment(sql, _parameters);
    }

    private string TranslateExpression(FilterExpression filter, LayerDefinition layer)
    {
        return filter switch
        {
            BinaryExpression bin => TranslateBinary(bin, layer),
            UnaryExpression un => TranslateUnary(un, layer),
            PropertyReference prop => TranslateProperty(prop, layer),
            Literal lit => TranslateLiteral(lit),
            SpatialPredicate spatial => TranslateSpatial(spatial, layer),
            SpatialDistancePredicate spatialDistance => TranslateSpatialDistance(spatialDistance, layer),
            TemporalPredicate temporal => TranslateTemporal(temporal, layer),
            ArrayPredicate array => TranslateArrayPredicate(array, layer),
            FunctionCall func => TranslateFunction(func, layer),
            IntervalLiteral interval => TranslateIntervalLiteral(interval),
            ArrayLiteral arrayLiteral => TranslateArrayLiteral(arrayLiteral),
            ValueList list => TranslateValueList(list, layer),
            _ => throw new NotSupportedException($"Unknown filter type: {filter.GetType()}")
        };
    }

    private string TranslateBinary(BinaryExpression binary, LayerDefinition layer)
    {
        var left = TranslateExpression(binary.Left, layer);
        var right = TranslateExpression(binary.Right, layer);

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

    private string TranslateUnary(UnaryExpression unary, LayerDefinition layer)
    {
        var operand = TranslateExpression(unary.Operand, layer);

        return unary.Operator switch
        {
            UnaryOperator.Not => $"NOT ({operand})",
            UnaryOperator.IsNull => $"{operand} IS NULL",
            UnaryOperator.IsNotNull => $"{operand} IS NOT NULL",
            UnaryOperator.Negate => $"-({operand})",
            _ => throw new NotSupportedException($"Unary operator {unary.Operator}")
        };
    }

    private string TranslateProperty(PropertyReference property, LayerDefinition layer)
    {
        // Validate field exists in layer
        var field = layer.Fields.FirstOrDefault(f => f.Name.Equals(property.PropertyName, StringComparison.OrdinalIgnoreCase));

        if (field == null &&
            _useJsonAttributes &&
            layer.PrimaryKeyField != null &&
            property.PropertyName.Equals("id", StringComparison.OrdinalIgnoreCase))
        {
            return QuoteIdentifier(_primaryKeyColumn);
        }

        if (field == null)
        {
            throw new ArgumentException($"Field '{property.PropertyName}' not found in layer '{layer.Name}'");
        }

        if (!_useJsonAttributes)
        {
            // Quote field name to handle reserved words and mixed-case names
            // PostgreSQL uses double quotes for identifiers
            return QuoteIdentifier(field.Name);
        }

        if (field.IsGeometry)
        {
            return GetGeometryColumnExpression(layer);
        }

        if (layer.PrimaryKeyField != null &&
            field.Name.Equals(layer.PrimaryKeyField.Name, StringComparison.OrdinalIgnoreCase))
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

    private string TranslateSpatial(SpatialPredicate spatial, LayerDefinition layer)
    {
        var left = TranslateGeometryExpression(spatial.Left, layer);
        var right = TranslateGeometryExpression(spatial.Right, layer);
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

    private string TranslateSpatialDistance(SpatialDistancePredicate spatial, LayerDefinition layer)
    {
        var left = TranslateGeometryExpression(spatial.Left, layer);
        var right = TranslateGeometryExpression(spatial.Right, layer);
        var distance = TranslateExpression(spatial.Distance, layer);

        return spatial.Operator switch
        {
            SpatialOperator.DWithin => $"ST_DWithin({left}, {right}, {distance})",
            SpatialOperator.Beyond => $"ST_Distance({left}, {right}) > {distance}",
            _ => throw new NotSupportedException($"Spatial operator {spatial.Operator}")
        };
    }

    private string TranslateGeometryExpression(FilterExpression expression, LayerDefinition layer)
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
                if (geometry.Srid != layer.SpatialReference.Srid)
                {
                    geometryExpression = $"ST_Transform({geometryExpression}, {layer.SpatialReference.Srid})";
                }

                return geometryExpression;
            }
            case PropertyReference property:
            {
                var field = layer.Fields.FirstOrDefault(f => f.Name.Equals(property.PropertyName, StringComparison.OrdinalIgnoreCase));
                if (field == null || !field.IsGeometry)
                {
                    throw new ArgumentException($"Field '{property.PropertyName}' is not a geometry field");
                }

                return GetGeometryColumnExpression(layer);
            }
            case FunctionCall functionCall:
                return TranslateFunction(functionCall, layer);
            default:
                throw new NotSupportedException($"Unsupported geometry expression: {expression.GetType()}");
        }
    }

    private string TranslateTemporal(TemporalPredicate temporal, LayerDefinition layer)
    {
        var left = TranslateTemporalExpression(temporal.Left, layer);
        var right = TranslateTemporalExpression(temporal.Right, layer);

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
            TemporalOperator.Before => $"{leftEnd} < {rightStart}",
            TemporalOperator.After => $"{leftStart} > {rightEnd}",
            TemporalOperator.Equals => $"({leftStart} = {rightStart} AND {leftEnd} = {rightEnd})",
            TemporalOperator.Disjoint => $"({leftEnd} < {rightStart} OR {leftStart} > {rightEnd})",
            TemporalOperator.Intersects => $"NOT ({leftEnd} < {rightStart} OR {leftStart} > {rightEnd})",
            TemporalOperator.Contains => $"({leftStart} < {rightStart} AND {leftEnd} > {rightEnd})",
            TemporalOperator.During => $"({leftStart} > {rightStart} AND {leftEnd} < {rightEnd})",
            TemporalOperator.Starts => $"({leftStart} = {rightStart} AND {leftEnd} < {rightEnd})",
            TemporalOperator.StartedBy => $"({leftStart} = {rightStart} AND {leftEnd} > {rightEnd})",
            TemporalOperator.Finishes => $"({leftEnd} = {rightEnd} AND {leftStart} > {rightStart})",
            TemporalOperator.FinishedBy => $"({leftEnd} = {rightEnd} AND {leftStart} < {rightStart})",
            TemporalOperator.Meets => $"{leftEnd} = {rightStart}",
            TemporalOperator.MetBy => $"{leftStart} = {rightEnd}",
            TemporalOperator.Overlaps => $"({leftStart} < {rightStart} AND {leftEnd} > {rightStart} AND {leftEnd} < {rightEnd})",
            TemporalOperator.OverlappedBy => $"({rightStart} < {leftStart} AND {rightEnd} > {leftStart} AND {rightEnd} < {leftEnd})",
            _ => throw new NotSupportedException($"Temporal operator {temporal.Operator}")
        };
    }

    private string TranslateArrayPredicate(ArrayPredicate array, LayerDefinition layer)
    {
        var left = TranslateArrayExpression(array.Left, layer);
        var right = TranslateArrayExpression(array.Right, layer);

        return array.Operator switch
        {
            ArrayOperator.Equals => $"{left} = {right}",
            ArrayOperator.Contains => $"{left} @> {right}",
            ArrayOperator.ContainedBy => $"{right} @> {left}",
            ArrayOperator.Overlaps => $"EXISTS (SELECT 1 FROM jsonb_array_elements({left}) AS l JOIN jsonb_array_elements({right}) AS r ON l.value = r.value)",
            _ => throw new NotSupportedException($"Array operator {array.Operator}")
        };
    }

    private string TranslateFunction(FunctionCall function, LayerDefinition layer)
    {
        var args = function.Arguments.Select(arg => TranslateExpression(arg, layer)).ToArray();
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
            "SUBSTRING" => $"SUBSTRING({argString})",
            "SUBSTR" => $"SUBSTRING({argString})",
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
            "COALESCE" => $"COALESCE({argString})",
            "NULLIF" => $"NULLIF({argString})",
            "POWER" => $"POWER({argString})",
            "MOD" => args.Length == 2
                ? $"MOD({CastNumeric(args[0])}, {CastNumeric(args[1])})"
                : throw new ArgumentException("MOD requires two arguments"),
            "CASEI" => $"LOWER({argString})",
            "ACCENTI" => $"UNACCENT(LOWER({argString}))",
            _ => throw new NotSupportedException($"Function {function.FunctionName}")
        };
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

    private string TranslateValueList(ValueList valueList, LayerDefinition layer)
    {
        var values = valueList.Values.Select(v => TranslateExpression(v, layer));
        return $"({string.Join(", ", values)})";
    }

    private static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static string EscapeSqlLiteral(string value)
        => value.Replace("'", "''");

    private static string CastNumeric(string sql)
        => $"({sql})::numeric";

    private string GetGeometryColumnExpression(LayerDefinition layer)
    {
        var geomColumnName = _useJsonAttributes ? _geometryColumn : layer.GeometryField?.Name ?? _geometryColumn;
        var quoted = QuoteIdentifier(geomColumnName);
        return $"{quoted}::geometry";
    }

    private string TranslateArrayExpression(FilterExpression expression, LayerDefinition layer)
    {
        if (expression is ArrayLiteral arrayLiteral)
        {
            return TranslateArrayLiteral(arrayLiteral);
        }

        if (expression is PropertyReference propertyReference)
        {
            var field = layer.Fields.FirstOrDefault(f => f.Name.Equals(propertyReference.PropertyName, StringComparison.OrdinalIgnoreCase));
            if (field == null || field.Type != FieldType.Json)
            {
                throw new ArgumentException($"Array predicates require JSON array fields. '{propertyReference.PropertyName}' is not JSON.");
            }

            var attributesColumn = QuoteIdentifier(_attributesColumn);
            var key = EscapeSqlLiteral(field.Name);
            return $"{attributesColumn} -> '{key}'";
        }

        throw new NotSupportedException($"Unsupported array expression: {expression.GetType()}");
    }

    private TemporalBounds TranslateTemporalExpression(FilterExpression expression, LayerDefinition layer)
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
                var field = layer.Fields.FirstOrDefault(f => f.Name.Equals(property.PropertyName, StringComparison.OrdinalIgnoreCase));
                var kind = field?.Type == FieldType.Date ? TemporalKind.Date : TemporalKind.Timestamp;
                var sql = TranslateProperty(property, layer);
                return new TemporalBounds(sql, sql, kind, false, false, false);
            }
            case FunctionCall functionCall:
            {
                var sql = TranslateFunction(functionCall, layer);
                return new TemporalBounds(sql, sql, TemporalKind.Timestamp, false, false, false);
            }
            default:
                throw new NotSupportedException($"Unsupported temporal expression: {expression.GetType()}");
        }
    }

    private void EnsureTemporalCompatibility(TemporalOperator op, TemporalBounds left, TemporalBounds right)
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

    private string NormalizeTemporalStart(TemporalBounds bounds)
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

    private string NormalizeTemporalEnd(TemporalBounds bounds)
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

    private enum TemporalKind
    {
        Date,
        Timestamp
    }

    private static string? GetJsonCastType(FieldType fieldType)
    {
        return fieldType switch
        {
            FieldType.Integer => "integer",
            FieldType.BigInteger => "bigint",
            FieldType.Float => "real",
            FieldType.Double => "double precision",
            FieldType.Boolean => "boolean",
            FieldType.DateTime => "timestamptz",
            FieldType.Date => "date",
            FieldType.Time => "time",
            FieldType.Uuid => "uuid",
            _ => null
        };
    }
}

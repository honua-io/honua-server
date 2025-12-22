// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;

namespace Honua.Core.Queries.Filters;

/// <summary>
/// Translates filter expressions to parameterized SQL WHERE clauses
/// </summary>
public sealed class SqlFilterTranslator
{
    private int _paramIndex;
    private readonly List<object?> _parameters = [];

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
            FunctionCall func => TranslateFunction(func, layer),
            ValueList list => TranslateValueList(list),
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
            _ => throw new NotSupportedException($"Binary operator {binary.Operator}")
        };

        // Handle logical operators with proper parentheses
        if (binary.Operator is BinaryOperator.And or BinaryOperator.Or)
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
            _ => throw new NotSupportedException($"Unary operator {unary.Operator}")
        };
    }

    private string TranslateProperty(PropertyReference property, LayerDefinition layer)
    {
        // Validate field exists in layer
        var field = layer.Fields.FirstOrDefault(f => f.Name.Equals(property.PropertyName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Field '{property.PropertyName}' not found in layer '{layer.Name}'");

        // Quote field name to handle reserved words and mixed-case names
        // PostgreSQL uses double quotes for identifiers
        return $"\"{field.Name}\"";
    }

    private string TranslateLiteral(Literal literal)
    {
        var paramName = $"@p{_paramIndex++}";
        _parameters.Add(literal.Value);
        return paramName;
    }

    private string TranslateSpatial(SpatialPredicate spatial, LayerDefinition layer)
    {
        var geomColumnName = layer.GeometryField?.Name ?? "geom";
        var geomColumn = $"\"{geomColumnName}\""; // Quote geometry column name
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

        var wkbParam = $"@p{_paramIndex++}";
        var sridParam = $"@p{_paramIndex++}";
        _parameters.Add(spatial.Geometry.Wkb);
        _parameters.Add(spatial.Geometry.Srid);

        return $"{function}({geomColumn}, ST_GeomFromWKB({wkbParam}, {sridParam}))";
    }

    private string TranslateFunction(FunctionCall function, LayerDefinition layer)
    {
        var args = function.Arguments.Select(arg => TranslateExpression(arg, layer));
        var argString = string.Join(", ", args);

        return function.FunctionName.ToUpperInvariant() switch
        {
            "UPPER" => $"UPPER({argString})",
            "LOWER" => $"LOWER({argString})",
            "LENGTH" => $"LENGTH({argString})",
            _ => throw new NotSupportedException($"Function {function.FunctionName}")
        };
    }

    private string TranslateValueList(ValueList valueList)
    {
        var values = valueList.Values.Select(v => TranslateLiteral(v));
        return $"({string.Join(", ", values)})";
    }
}

/// <summary>
/// Represents a SQL fragment with parameterized values
/// </summary>
/// <param name="Sql">The SQL string with parameter placeholders</param>
/// <param name="Parameters">Parameter values in order</param>
public record SqlFragment(string Sql, IReadOnlyList<object?> Parameters);

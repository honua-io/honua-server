// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Queries.Filters;

/// <summary>
/// Normalizes filter expressions by coercing literals to match layer field types.
/// </summary>
public static class FilterExpressionNormalizer
{
    /// <summary>
    /// Maximum allowed nesting depth for filter expressions.
    /// Prevents maliciously deep inputs from overflowing recursive normalization/translation paths.
    /// </summary>
    public const int MaxExpressionDepth = 50;

    /// <summary>
    /// Normalizes a filter expression for the provided layer definition.
    /// </summary>
    /// <param name="expression">Filter expression to normalize.</param>
    /// <param name="layer">Layer definition used for type coercion.</param>
    /// <returns>Normalized filter expression.</returns>
    public static FilterExpression Normalize(FilterExpression expression, LayerDefinition layer)
    {
        EnsureWithinMaxDepth(expression);
        return NormalizeCore(expression, layer);
    }

    private static FilterExpression NormalizeCore(FilterExpression expression, LayerDefinition layer)
    {
        return expression switch
        {
            BinaryExpression binary => NormalizeBinaryExpression(binary, layer),
            UnaryExpression unary => new UnaryExpression(unary.Operator, NormalizeCore(unary.Operand, layer)),
            SpatialPredicate spatial => new SpatialPredicate(spatial.Operator,
                NormalizeCore(spatial.Left, layer),
                NormalizeCore(spatial.Right, layer)),
            SpatialDistancePredicate spatialDistance => new SpatialDistancePredicate(
                spatialDistance.Operator,
                NormalizeCore(spatialDistance.Left, layer),
                NormalizeCore(spatialDistance.Right, layer),
                NormalizeCore(spatialDistance.Distance, layer)),
            TemporalPredicate temporal => new TemporalPredicate(
                temporal.Operator,
                NormalizeCore(temporal.Left, layer),
                NormalizeCore(temporal.Right, layer)),
            ArrayPredicate array => new ArrayPredicate(
                array.Operator,
                NormalizeCore(array.Left, layer),
                NormalizeCore(array.Right, layer)),
            FunctionCall function => new FunctionCall(function.FunctionName,
                function.Arguments.Select(arg => NormalizeCore(arg, layer)).ToArray()),
            ArrayLiteral arrayLiteral => new ArrayLiteral(arrayLiteral.Elements.Select(arg => NormalizeCore(arg, layer)).ToArray()),
            ValueList valueList => new ValueList(valueList.Values.Select(arg => NormalizeCore(arg, layer)).ToArray()),
            _ => expression
        };
    }

    private static BinaryExpression NormalizeBinaryExpression(BinaryExpression binary, LayerDefinition layer)
    {
        var left = NormalizeCore(binary.Left, layer);
        var right = NormalizeCore(binary.Right, layer);

        if (binary.Operator is BinaryOperator.Equal or BinaryOperator.NotEqual or
            BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual or
            BinaryOperator.LessThan or BinaryOperator.LessThanOrEqual)
        {
            if (left is PropertyReference property && right is Literal literal)
            {
                right = CoerceLiteral(property, literal, layer);
            }
            else if (right is PropertyReference propertyRight && left is Literal literalLeft)
            {
                left = CoerceLiteral(propertyRight, literalLeft, layer);
            }
        }

        return new BinaryExpression(left, binary.Operator, right);
    }

    private static void EnsureWithinMaxDepth(FilterExpression expression)
    {
        var stack = new Stack<(FilterExpression Expression, int Depth)>();
        stack.Push((expression, 1));

        while (stack.Count > 0)
        {
            var (current, depth) = stack.Pop();
            if (depth > MaxExpressionDepth)
            {
                throw new ArgumentException(
                    $"Filter expression exceeds the maximum nesting depth of {MaxExpressionDepth}.");
            }

            foreach (var child in EnumerateChildren(current))
            {
                stack.Push((child, depth + 1));
            }
        }
    }

    private static IEnumerable<FilterExpression> EnumerateChildren(FilterExpression expression)
    {
        switch (expression)
        {
            case BinaryExpression binary:
                yield return binary.Left;
                yield return binary.Right;
                yield break;
            case UnaryExpression unary:
                yield return unary.Operand;
                yield break;
            case SpatialPredicate spatial:
                yield return spatial.Left;
                yield return spatial.Right;
                yield break;
            case SpatialDistancePredicate spatialDistance:
                yield return spatialDistance.Left;
                yield return spatialDistance.Right;
                yield return spatialDistance.Distance;
                yield break;
            case TemporalPredicate temporal:
                yield return temporal.Left;
                yield return temporal.Right;
                yield break;
            case ArrayPredicate array:
                yield return array.Left;
                yield return array.Right;
                yield break;
            case FunctionCall function:
                foreach (var argument in function.Arguments)
                {
                    yield return argument;
                }

                yield break;
            case ArrayLiteral arrayLiteral:
                foreach (var element in arrayLiteral.Elements)
                {
                    yield return element;
                }

                yield break;
            case ValueList valueList:
                foreach (var value in valueList.Values)
                {
                    yield return value;
                }

                yield break;
            case IntervalLiteral interval when interval.Start is not null:
                yield return interval.Start;
                if (interval.End is not null)
                {
                    yield return interval.End;
                }

                yield break;
            case IntervalLiteral interval when interval.End is not null:
                yield return interval.End;
                yield break;
        }
    }

    private static Literal CoerceLiteral(PropertyReference property, Literal literal, LayerDefinition layer)
    {
        if (!TryGetFieldType(layer, property.PropertyName, out var fieldType))
        {
            throw new ArgumentException($"Unknown field '{property.PropertyName}' in filter expression.");
        }

        return fieldType switch
        {
            FieldType.DateTime => CoerceDateTimeLiteral(property.PropertyName, literal),
            FieldType.Date => CoerceDateLiteral(property.PropertyName, literal),
            FieldType.Boolean => CoerceBooleanLiteral(property.PropertyName, literal),
            FieldType.Integer or FieldType.BigInteger or FieldType.Float or FieldType.Double
                => CoerceNumericLiteral(property.PropertyName, literal),
            _ => literal
        };
    }

    private static Literal CoerceDateTimeLiteral(string propertyName, Literal literal)
    {
        if (literal.Type == LiteralType.DateTime)
        {
            return literal;
        }

        if (literal.Type == LiteralType.Date && literal.Value is DateOnly dateOnly)
        {
            var timestamp = dateOnly.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            return new Literal(new DateTimeOffset(timestamp), LiteralType.DateTime);
        }

        if (literal.Type == LiteralType.Text && literal.Value is string text &&
            DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return new Literal(parsed, LiteralType.DateTime);
        }

        throw new ArgumentException($"Field '{propertyName}' expects a datetime value.");
    }

    private static Literal CoerceDateLiteral(string propertyName, Literal literal)
    {
        if (literal.Type == LiteralType.Date)
        {
            return literal;
        }

        if (literal.Type == LiteralType.DateTime && literal.Value is DateTimeOffset dto)
        {
            return new Literal(DateOnly.FromDateTime(dto.UtcDateTime), LiteralType.Date);
        }

        if (literal.Type == LiteralType.Text && literal.Value is string text &&
            DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return new Literal(parsed, LiteralType.Date);
        }

        throw new ArgumentException($"Field '{propertyName}' expects a date value.");
    }

    private static Literal CoerceBooleanLiteral(string propertyName, Literal literal)
    {
        if (literal.Type == LiteralType.Boolean)
        {
            return literal;
        }

        if (literal.Type == LiteralType.Text && literal.Value is string text &&
            bool.TryParse(text, out var parsed))
        {
            return new Literal(parsed, LiteralType.Boolean);
        }

        throw new ArgumentException($"Field '{propertyName}' expects a boolean value.");
    }

    private static Literal CoerceNumericLiteral(string propertyName, Literal literal)
    {
        if (literal.Type == LiteralType.Number)
        {
            return literal;
        }

        throw new ArgumentException($"Field '{propertyName}' expects a numeric value.");
    }

    private static bool TryGetFieldType(LayerDefinition layer, string field, out FieldType fieldType)
    {
        if (field.Equals(FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase))
        {
            fieldType = FieldType.BigInteger;
            return true;
        }

        if (field.Equals("layerid", StringComparison.OrdinalIgnoreCase) ||
            field.Equals("layer_id", StringComparison.OrdinalIgnoreCase))
        {
            fieldType = FieldType.Integer;
            return true;
        }

        if (field.Equals("geometry", StringComparison.OrdinalIgnoreCase) ||
            field.Equals("shape", StringComparison.OrdinalIgnoreCase))
        {
            fieldType = FieldType.Geometry;
            return true;
        }

        if (field.Equals("created_at", StringComparison.OrdinalIgnoreCase) ||
            field.Equals("updated_at", StringComparison.OrdinalIgnoreCase))
        {
            fieldType = FieldType.DateTime;
            return true;
        }

        var fieldDefinition = layer.Fields.FirstOrDefault(f =>
            f.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
        if (fieldDefinition != null)
        {
            fieldType = fieldDefinition.Type;
            return true;
        }

        fieldType = FieldType.String;
        return false;
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.GeoServicesSql;

namespace Honua.Server.Features.Protocols.GeoServices.ImageServer.Services;

/// <summary>
/// Evaluates an ArcGIS-SQL <c>where</c> string against an in-memory raster catalog page.
/// The catalog is normally tiny per layer, so a SQL emitter is unnecessary; instead the
/// shared <see cref="GeoServicesSqlParser"/> AST is reused and walked here.
/// </summary>
internal interface IImageServerCatalogFilterEvaluator
{
    /// <summary>
    /// Filters the supplied catalog items by the supplied WHERE clause.
    /// </summary>
    /// <exception cref="ImageServerCatalogFilterException">
    /// Thrown when the WHERE clause is malformed or references an unknown field.
    /// </exception>
    IEnumerable<ImageServerCatalogItem> Apply(
        IEnumerable<ImageServerCatalogItem> items,
        string whereClause);
}

/// <summary>
/// Default in-memory evaluator for raster catalog WHERE clauses.
/// </summary>
internal sealed class ImageServerCatalogFilterEvaluator : IImageServerCatalogFilterEvaluator
{
    public IEnumerable<ImageServerCatalogItem> Apply(
        IEnumerable<ImageServerCatalogItem> items,
        string whereClause)
    {
        ArgumentNullException.ThrowIfNull(items);

        FilterExpression expression;
        try
        {
            expression = new GeoServicesSqlParser().Parse(whereClause);
        }
        catch (ArgumentException ex)
        {
            throw new ImageServerCatalogFilterException(ex.Message, ex);
        }

        var result = new List<ImageServerCatalogItem>();
        foreach (var item in items)
        {
            if (Evaluate(expression, item))
            {
                result.Add(item);
            }
        }

        return result;
    }

    private static bool Evaluate(FilterExpression expression, ImageServerCatalogItem item)
    {
        return expression switch
        {
            BinaryExpression binary => EvaluateBinary(binary, item),
            UnaryExpression unary => EvaluateUnary(unary, item),
            _ => throw new ImageServerCatalogFilterException(
                $"Unsupported filter expression at root: {expression.GetType().Name}.")
        };
    }

    private static bool EvaluateBinary(BinaryExpression expression, ImageServerCatalogItem item)
    {
        switch (expression.Operator)
        {
            case BinaryOperator.And:
                return Evaluate(expression.Left, item) && Evaluate(expression.Right, item);
            case BinaryOperator.Or:
                return Evaluate(expression.Left, item) || Evaluate(expression.Right, item);
        }

        var left = ResolveValue(expression.Left, item);
        var right = ResolveValue(expression.Right, item);

        return expression.Operator switch
        {
            BinaryOperator.Equal => CompareEquality(left, right),
            BinaryOperator.NotEqual => !CompareEquality(left, right),
            BinaryOperator.LessThan => Compare(left, right) < 0,
            BinaryOperator.LessThanOrEqual => Compare(left, right) <= 0,
            BinaryOperator.GreaterThan => Compare(left, right) > 0,
            BinaryOperator.GreaterThanOrEqual => Compare(left, right) >= 0,
            BinaryOperator.Like => MatchLike(left, right, negate: false),
            BinaryOperator.NotLike => MatchLike(left, right, negate: true),
            BinaryOperator.In => MatchIn(left, expression.Right, item, negate: false),
            BinaryOperator.NotIn => MatchIn(left, expression.Right, item, negate: true),
            _ => throw new ImageServerCatalogFilterException(
                $"Unsupported binary operator '{expression.Operator}' in raster catalog WHERE clause.")
        };
    }

    private static bool EvaluateUnary(UnaryExpression expression, ImageServerCatalogItem item)
    {
        return expression.Operator switch
        {
            UnaryOperator.Not => !Evaluate(expression.Operand, item),
            UnaryOperator.IsNull => ResolveValue(expression.Operand, item) is null,
            UnaryOperator.IsNotNull => ResolveValue(expression.Operand, item) is not null,
            _ => throw new ImageServerCatalogFilterException(
                $"Unsupported unary operator '{expression.Operator}' in raster catalog WHERE clause.")
        };
    }

    private static object? ResolveValue(FilterExpression expression, ImageServerCatalogItem item)
    {
        return expression switch
        {
            PropertyReference property => ResolveProperty(property.PropertyName, item),
            Literal literal => literal.Value,
            _ => throw new ImageServerCatalogFilterException(
                $"Unsupported expression in raster catalog WHERE clause: {expression.GetType().Name}.")
        };
    }

    private static object? ResolveProperty(string propertyName, ImageServerCatalogItem item)
    {
        // Esri raster catalog field names are case-insensitive.
        return propertyName.ToUpperInvariant() switch
        {
            "OBJECTID" => item.ObjectId,
            "NAME" => item.Name,
            "MINPS" => item.MinPixelSize,
            "MAXPS" => item.MaxPixelSize,
            "LOWPS" => item.LowPixelSize,
            "HIGHPS" => item.HighPixelSize,
            "CENTERX" => item.CenterX,
            "CENTERY" => item.CenterY,
            "ZORDER" => item.ZOrder,
            "SHAPE_LENGTH" => item.ShapeLength,
            "SHAPE_AREA" => item.ShapeArea,
            "BANDCOUNT" or "NUM_BANDS" => item.BandCount,
            "PIXELTYPE" or "PIXEL_TYPE" => item.PixelType,
            "ACQUISITIONDATE" => item.AcquisitionDate?.UtcDateTime,
            "CREATEDAT" or "CREATED_AT" => item.CreatedAt.UtcDateTime,
            _ => throw new ImageServerCatalogFilterException(
                $"Unknown raster catalog field '{propertyName}'.")
        };
    }

    private static bool CompareEquality(object? left, object? right)
    {
        if (left is null && right is null)
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        if (TryCoerceToDouble(left, out var leftDouble) && TryCoerceToDouble(right, out var rightDouble))
        {
            return leftDouble.Equals(rightDouble);
        }

        return string.Equals(left.ToString(), right.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static int Compare(object? left, object? right)
    {
        if (TryCoerceToDouble(left, out var leftDouble) && TryCoerceToDouble(right, out var rightDouble))
        {
            return leftDouble.CompareTo(rightDouble);
        }

        if (left is DateTime leftDate && right is DateTime rightDate)
        {
            return DateTime.Compare(leftDate, rightDate);
        }

        return string.Compare(left?.ToString(), right?.ToString(), StringComparison.Ordinal);
    }

    private static bool MatchLike(object? left, object? right, bool negate)
    {
        if (left is null || right is null)
        {
            return negate;
        }

        var pattern = right.ToString() ?? string.Empty;
        var input = left.ToString() ?? string.Empty;

        // Translate SQL LIKE wildcards into a simple comparison.
        var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("%", ".*", StringComparison.Ordinal)
            .Replace("_", ".", StringComparison.Ordinal) + "$";
        var matched = System.Text.RegularExpressions.Regex.IsMatch(
            input,
            regexPattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(50));
        return negate ? !matched : matched;
    }

    private static bool MatchIn(object? left, FilterExpression rightExpression, ImageServerCatalogItem item, bool negate)
    {
        if (rightExpression is not ValueList values)
        {
            throw new ImageServerCatalogFilterException("Right operand of IN must be a value list.");
        }

        foreach (var value in values.Values)
        {
            var resolved = ResolveValue(value, item);
            if (CompareEquality(left, resolved))
            {
                return !negate;
            }
        }

        return negate;
    }

    private static bool TryCoerceToDouble(object? value, out double result)
    {
        switch (value)
        {
            case null:
                result = 0;
                return false;
            case double d:
                result = d;
                return true;
            case float f:
                result = f;
                return true;
            case long l:
                result = l;
                return true;
            case int i:
                result = i;
                return true;
            case decimal m:
                result = (double)m;
                return true;
            case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                result = parsed;
                return true;
            default:
                result = 0;
                return false;
        }
    }
}

/// <summary>
/// Surfaces a parser/evaluator failure to the calling endpoint as a 400.
/// </summary>
internal sealed class ImageServerCatalogFilterException : Exception
{
    public ImageServerCatalogFilterException(string message)
        : base(message)
    {
    }

    public ImageServerCatalogFilterException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

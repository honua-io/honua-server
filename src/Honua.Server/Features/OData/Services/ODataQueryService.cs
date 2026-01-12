// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Infrastructure.Validation;

namespace Honua.Server.Features.OData.Services;

/// <summary>
/// Log category for OData query operations.
/// </summary>
internal sealed class ODataQueryLog;

/// <summary>
/// Service for handling OData query operations including filtering, ordering, pagination, and field selection.
/// Converts OData query parameters to SQL fragments and handles query result processing.
/// </summary>
internal sealed partial class ODataQueryService
{
    private readonly IFilterExpressionService _filterExpressionService;

    /// <summary>
    /// Initializes a new instance of the <see cref="ODataQueryService"/> class.
    /// </summary>
    public ODataQueryService(IFilterExpressionService filterExpressionService)
    {
        _filterExpressionService = filterExpressionService ?? throw new ArgumentNullException(nameof(filterExpressionService));
    }

    /// <summary>
    /// Builds a feature query from OData parameters with proper validation and conversion.
    /// </summary>
    public FeatureQuery BuildFeatureQuery(
        string? filter,
        string? orderby,
        int? resultRecordCount,
        int? resultOffset,
        LayerDefinition layer,
        out string? error)
    {
        error = null;

        SqlFragment? sqlFilter = null;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            try
            {
                sqlFilter = ConvertODataFilterToSqlFragment(filter, layer);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                error = ex.Message;
                return new FeatureQuery();
            }
        }

        return new FeatureQuery
        {
            Where = null,
            SqlFilter = sqlFilter,
            SpatialFilter = null,
            SpatialReferenceSrid = layer.SpatialReference.ToSrid(),
            OrderBy = OrderByParsing.ParseODataOrderBy(orderby, layer),
            Limit = resultRecordCount,
            Offset = resultOffset
        };
    }

    /// <summary>
    /// Applies basic filtering to layer collections using simple OData expressions.
    /// </summary>
    public IEnumerable<LayerDefinition> ApplyBasicFilter(
        IEnumerable<LayerDefinition> layers,
        string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return layers;
        }

        var parseResult = _filterExpressionService.Parse(FilterLanguage.OData, filter);
        if (!parseResult.IsSuccess)
        {
            throw new ArgumentException(parseResult.ErrorMessage ?? "Invalid OData filter.");
        }

        if (parseResult.Expression == null)
        {
            return layers;
        }

        return layers.Where(layer => EvaluateLayerFilter(parseResult.Expression, layer));
    }

    /// <summary>
    /// Applies field selection to result objects using an AOT-compatible approach.
    /// </summary>
    public object[] ApplyFieldSelection(Dictionary<string, object?>[] data, string select)
    {
        var fields = select.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (fields.Contains("*"))
        {
            return data.Cast<object>().ToArray();
        }

        return data.Select(item =>
        {
            var dict = new Dictionary<string, object?>();

            if (item is IDictionary<string, object?> existingDict)
            {
                // If it's already a dictionary, filter based on selected fields
                foreach (var kvp in existingDict)
                {
                    if (kvp.Key.StartsWith("@odata.", StringComparison.OrdinalIgnoreCase) ||
                        ODataUtilityService.IsKeyProperty(kvp.Key) ||
                        fields.Contains(kvp.Key))
                    {
                        dict[kvp.Key] = kvp.Value;
                    }
                }
            }

            return dict;
        }).ToArray();
    }

    /// <summary>
    /// Converts an OData $filter expression into a parameterized SQL fragment.
    /// </summary>
    public SqlFragment? ConvertODataFilterToSqlFragment(string? odataFilter, LayerDefinition layer)
    {
        if (string.IsNullOrWhiteSpace(odataFilter))
        {
            return null;
        }

        var translationResult = _filterExpressionService.Translate(FilterLanguage.OData, odataFilter, layer);
        if (!translationResult.IsSuccess)
        {
            throw new ArgumentException(translationResult.ErrorMessage ?? "Invalid OData filter.");
        }

        return translationResult.SqlFilter;
    }


    private static bool EvaluateLayerFilter(FilterExpression expression, LayerDefinition layer)
    {
        var result = EvaluateExpression(expression, layer);
        if (result is bool booleanResult)
        {
            return booleanResult;
        }

        throw new ArgumentException("OData filter did not evaluate to a boolean expression.");
    }

    private static object? EvaluateExpression(FilterExpression expression, LayerDefinition layer)
    {
        return expression switch
        {
            BinaryExpression binary => EvaluateBinary(binary, layer),
            UnaryExpression unary => EvaluateUnary(unary, layer),
            PropertyReference property => GetLayerPropertyValue(layer, property.PropertyName),
            Literal literal => literal.Value,
            FunctionCall function => EvaluateFunction(function, layer),
            _ => throw new ArgumentException($"Unsupported OData filter expression: {expression.GetType().Name}")
        };
    }

    private static object? EvaluateBinary(BinaryExpression expression, LayerDefinition layer)
    {
        if (expression.Operator is BinaryOperator.And or BinaryOperator.Or)
        {
            var leftBool = ToBoolean(EvaluateExpression(expression.Left, layer));
            var rightBool = ToBoolean(EvaluateExpression(expression.Right, layer));
            return expression.Operator == BinaryOperator.And ? leftBool && rightBool : leftBool || rightBool;
        }

        if (expression.Operator is BinaryOperator.Add or BinaryOperator.Subtract or BinaryOperator.Multiply or BinaryOperator.Divide or BinaryOperator.Modulo)
        {
            var leftNumber = ToNumber(EvaluateExpression(expression.Left, layer));
            var rightNumber = ToNumber(EvaluateExpression(expression.Right, layer));

            return expression.Operator switch
            {
                BinaryOperator.Add => leftNumber + rightNumber,
                BinaryOperator.Subtract => leftNumber - rightNumber,
                BinaryOperator.Multiply => leftNumber * rightNumber,
                BinaryOperator.Divide => rightNumber == 0 ? throw new ArgumentException("Division by zero.") : leftNumber / rightNumber,
                BinaryOperator.Modulo => leftNumber % rightNumber,
                _ => throw new ArgumentException($"Unsupported arithmetic operator {expression.Operator}.")
            };
        }

        var left = EvaluateExpression(expression.Left, layer);
        var right = EvaluateExpression(expression.Right, layer);

        return expression.Operator switch
        {
            BinaryOperator.Equal => AreEqual(left, right),
            BinaryOperator.NotEqual => !AreEqual(left, right),
            BinaryOperator.GreaterThan => Compare(left, right) > 0,
            BinaryOperator.GreaterThanOrEqual => Compare(left, right) >= 0,
            BinaryOperator.LessThan => Compare(left, right) < 0,
            BinaryOperator.LessThanOrEqual => Compare(left, right) <= 0,
            _ => throw new ArgumentException($"Unsupported binary operator {expression.Operator}.")
        };
    }

    private static bool EvaluateUnary(UnaryExpression expression, LayerDefinition layer)
    {
        var operand = EvaluateExpression(expression.Operand, layer);
        return expression.Operator switch
        {
            UnaryOperator.Not => !ToBoolean(operand),
            UnaryOperator.IsNull => operand == null,
            UnaryOperator.IsNotNull => operand != null,
            _ => throw new ArgumentException($"Unsupported unary operator {expression.Operator}.")
        };
    }

    private static object? EvaluateFunction(FunctionCall function, LayerDefinition layer)
    {
        var name = function.FunctionName.ToUpperInvariant();
        var args = function.Arguments.Select(arg => EvaluateExpression(arg, layer)).ToArray();

        return name switch
        {
            "POSITION" => EvaluatePosition(args),
            "LOWER" => args.Length == 1 ? Convert.ToString(args[0], CultureInfo.InvariantCulture)?.ToLowerInvariant() : throw new ArgumentException("LOWER requires one argument."),
            "UPPER" => args.Length == 1 ? Convert.ToString(args[0], CultureInfo.InvariantCulture)?.ToUpperInvariant() : throw new ArgumentException("UPPER requires one argument."),
            "TRIM" => args.Length == 1 ? Convert.ToString(args[0], CultureInfo.InvariantCulture)?.Trim() : throw new ArgumentException("TRIM requires one argument."),
            "LENGTH" => args.Length == 1 ? (Convert.ToString(args[0], CultureInfo.InvariantCulture)?.Length ?? 0) : throw new ArgumentException("LENGTH requires one argument."),
            "SUBSTRING" => EvaluateSubstring(args),
            "REPLACE" => args.Length == 3
                ? Convert.ToString(args[0], CultureInfo.InvariantCulture)?.Replace(
                    Convert.ToString(args[1], CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToString(args[2], CultureInfo.InvariantCulture) ?? string.Empty,
                    StringComparison.Ordinal)
                : throw new ArgumentException("REPLACE requires three arguments."),
            "CONCAT" => string.Concat(args.Select(a => Convert.ToString(a, CultureInfo.InvariantCulture))),
            "NOW" => args.Length == 0 ? DateTimeOffset.UtcNow : throw new ArgumentException("NOW does not accept arguments."),
            _ => throw new ArgumentException($"Unsupported function '{function.FunctionName}'.")
        };
    }

    private static int EvaluatePosition(object?[] args)
    {
        if (args.Length != 2)
        {
            throw new ArgumentException("POSITION requires two arguments.");
        }

        var needle = Convert.ToString(args[0], CultureInfo.InvariantCulture) ?? string.Empty;
        var haystack = Convert.ToString(args[1], CultureInfo.InvariantCulture) ?? string.Empty;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        return index < 0 ? 0 : index + 1;
    }

    private static string? EvaluateSubstring(object?[] args)
    {
        if (args.Length is < 2 or > 3)
        {
            throw new ArgumentException("SUBSTRING requires 2 or 3 arguments.");
        }

        var value = Convert.ToString(args[0], CultureInfo.InvariantCulture) ?? string.Empty;
        var start = (int)ToNumber(args[1]) - 1;
        if (start < 0)
        {
            start = 0;
        }

        if (args.Length == 2)
        {
            return start >= value.Length ? string.Empty : value[start..];
        }

        var length = (int)ToNumber(args[2]);
        if (length <= 0 || start >= value.Length)
        {
            return string.Empty;
        }

        var maxLength = Math.Min(length, value.Length - start);
        return value.Substring(start, maxLength);
    }

    private static object? GetLayerPropertyValue(LayerDefinition layer, string propertyName)
    {
        return propertyName.ToLowerInvariant() switch
        {
            "id" => layer.Id,
            "name" => layer.Name,
            "description" => layer.Description,
            "geometrytype" => layer.GeometryType.ToString(),
            "srid" => layer.SpatialReference.ToSrid(),
            _ => throw new ArgumentException($"Unknown layer property '{propertyName}'.")
        };
    }

    private static bool AreEqual(object? left, object? right)
    {
        if (left == null && right == null)
        {
            return true;
        }

        if (left == null || right == null)
        {
            return false;
        }

        if (left is string leftString && right is string rightString)
        {
            return string.Equals(leftString, rightString, StringComparison.OrdinalIgnoreCase);
        }

        if (IsNumber(left) && IsNumber(right))
        {
            return Math.Abs(ToNumber(left) - ToNumber(right)) < 0.0000001;
        }

        if (TryGetDateTimeOffset(left, out var leftDate) && TryGetDateTimeOffset(right, out var rightDate))
        {
            return leftDate.Equals(rightDate);
        }

        return left.Equals(right);
    }

    private static int Compare(object? left, object? right)
    {
        if (left == null || right == null)
        {
            throw new ArgumentException("Cannot compare null values.");
        }

        if (IsNumber(left) && IsNumber(right))
        {
            return ToNumber(left).CompareTo(ToNumber(right));
        }

        if (TryGetDateTimeOffset(left, out var leftDate) && TryGetDateTimeOffset(right, out var rightDate))
        {
            return leftDate.CompareTo(rightDate);
        }

        if (left is string leftString && right is string rightString)
        {
            return string.Compare(leftString, rightString, StringComparison.OrdinalIgnoreCase);
        }

        throw new ArgumentException("Unsupported comparison types in OData filter.");
    }

    private static bool ToBoolean(object? value)
    {
        if (value is bool boolValue)
        {
            return boolValue;
        }

        throw new ArgumentException("Expected boolean value in OData filter.");
    }

    private static double ToNumber(object? value)
    {
        if (value == null)
        {
            throw new ArgumentException("Expected numeric value in OData filter.");
        }

        return value switch
        {
            int i => i,
            long l => l,
            float f => f,
            double d => d,
            decimal m => (double)m,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => throw new ArgumentException($"Unsupported numeric value '{value}'.")
        };
    }

    private static bool IsNumber(object value)
        => value is int or long or float or double or decimal;

    private static bool TryGetDateTimeOffset(object value, out DateTimeOffset dateTimeOffset)
    {
        dateTimeOffset = default;

        if (value is DateTimeOffset dto)
        {
            dateTimeOffset = dto;
            return true;
        }

        if (value is DateTime dateTime)
        {
            dateTimeOffset = new DateTimeOffset(dateTime);
            return true;
        }

        if (value is DateOnly dateOnly)
        {
            dateTimeOffset = new DateTimeOffset(dateOnly.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            return true;
        }

        return false;
    }

}

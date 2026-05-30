// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Query;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Validation;

namespace Honua.Protocols.OData.Services;

/// <summary>
/// Service for handling OData query operations including filtering, ordering, pagination, and field selection.
/// Converts OData query parameters to SQL fragments and handles query result processing.
/// </summary>
internal sealed partial class ODataQueryService
{
    private readonly IFilterExpressionService _filterExpressionService;
    private readonly IQueryParameterAdapter<ODataQueryParameters> _queryParameterAdapter;
    private readonly IQueryProcessor _queryProcessor;

    /// <summary>
    /// Initializes a new instance of the <see cref="ODataQueryService"/> class.
    /// </summary>
    public ODataQueryService(
        IFilterExpressionService filterExpressionService,
        IQueryParameterAdapter<ODataQueryParameters> queryParameterAdapter,
        IQueryProcessor queryProcessor)
    {
        _filterExpressionService = filterExpressionService ?? throw new ArgumentNullException(nameof(filterExpressionService));
        _queryParameterAdapter = queryParameterAdapter ?? throw new ArgumentNullException(nameof(queryParameterAdapter));
        _queryProcessor = queryProcessor ?? throw new ArgumentNullException(nameof(queryProcessor));
    }

    /// <summary>
    /// Routes OData query parameters through the V2 <see cref="IQueryParameterAdapter{TProtocolParams}.ConvertAsync(TProtocolParams, MetadataV2Resource, CancellationToken)"/>
    /// path and the V2 <see cref="IQueryProcessor"/> overloads so $filter SQL translation
    /// and $select validation honour the canonical resource's <see cref="MetadataV2Resource.SchemaFields"/>.
    /// </summary>
    public async Task<(FeatureQuery Query, string? Error)> BuildFeatureQueryAsync(
        string? filter,
        string? orderby,
        int? resultRecordCount,
        int? resultOffset,
        MetadataV2Resource resource,
        string? select = null,
        string? expand = null,
        bool? count = null,
        string? compute = null,
        string? format = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var conversion = await _queryParameterAdapter.ConvertAsync(
            new ODataQueryParameters
            {
                Filter = filter,
                OrderBy = orderby,
                Top = resultRecordCount,
                Skip = resultOffset,
                Select = select,
                Expand = expand,
                Count = count,
                Compute = compute,
                Format = format
            },
            resource,
            cancellationToken).ConfigureAwait(false);

        if (!conversion.IsSuccess || conversion.Query == null)
        {
            return (new FeatureQuery(), conversion.ErrorMessage ?? "Invalid OData query.");
        }

        var validation = _queryProcessor.ValidateQuery(conversion.Query.Value, resource);
        if (!validation.IsValid)
        {
            return (new FeatureQuery(), validation.ErrorMessage ?? "Invalid OData query.");
        }

        var optimized = _queryProcessor.OptimizeQuery(conversion.Query.Value, resource);
        return (_queryProcessor.ToFeatureQuery(optimized, resource), null);
    }

    /// <summary>
    /// Applies basic filtering to projected OData layer payloads.
    /// </summary>
    public IEnumerable<Dictionary<string, object?>> ApplyBasicFilter(
        IEnumerable<Dictionary<string, object?>> layerPayloads,
        string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return layerPayloads;
        }

        var parseResult = _filterExpressionService.Parse(FilterLanguage.OData, filter);
        if (!parseResult.IsSuccess)
        {
            throw new ArgumentException(parseResult.ErrorMessage ?? "Invalid OData filter.");
        }

        if (parseResult.Expression == null)
        {
            return layerPayloads;
        }

        return layerPayloads.Where(payload => EvaluateLayerPayloadFilter(parseResult.Expression, payload));
    }

    /// <summary>
    /// Applies field selection to result objects using an AOT-compatible approach.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Kept as an instance method to preserve the current service API shape.")]
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
                    var isExpandedNavigation = kvp.Value is object?[] || kvp.Value is Dictionary<string, object?>;
                    if (kvp.Key.StartsWith("@odata.", StringComparison.OrdinalIgnoreCase) ||
                        ODataUtilityService.IsKeyProperty(kvp.Key) ||
                        isExpandedNavigation ||
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
    /// Parses the OData filter, then routes through the V2 <see cref="IFilterExpressionService.Translate(FilterExpression?, MetadataV2Resource)"/>
    /// path so SQL translation honours the resource's <see cref="MetadataV2Resource.SchemaFields"/>
    /// (typed field set) instead of the v1 layer's attribute fields.
    /// </summary>
    public SqlFragment? ConvertODataFilterToSqlFragment(string? odataFilter, MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (string.IsNullOrWhiteSpace(odataFilter))
        {
            return null;
        }

        var parseResult = _filterExpressionService.Parse(FilterLanguage.OData, odataFilter);
        if (!parseResult.IsSuccess)
        {
            throw new ArgumentException(parseResult.ErrorMessage ?? "Invalid OData filter.");
        }

        if (parseResult.Expression is null)
        {
            return null;
        }

        var translationResult = _filterExpressionService.Translate(parseResult.Expression, resource);
        if (!translationResult.IsSuccess)
        {
            throw new ArgumentException(translationResult.ErrorMessage ?? "Invalid OData filter.");
        }

        return translationResult.SqlFilter;
    }


    private static bool EvaluateLayerPayloadFilter(
        FilterExpression expression,
        IReadOnlyDictionary<string, object?> payload)
    {
        var result = EvaluateExpression(expression, payload);
        if (result is bool booleanResult)
        {
            return booleanResult;
        }

        throw new ArgumentException("OData filter did not evaluate to a boolean expression.");
    }

    private static object? EvaluateExpression(
        FilterExpression expression,
        IReadOnlyDictionary<string, object?> payload)
    {
        return expression switch
        {
            BinaryExpression binary => EvaluateBinary(binary, payload),
            UnaryExpression unary => EvaluateUnary(unary, payload),
            PropertyReference property => GetLayerPayloadPropertyValue(payload, property.PropertyName),
            Literal literal => literal.Value,
            FunctionCall function => EvaluateFunction(function, payload),
            _ => throw new ArgumentException($"Unsupported OData filter expression: {expression.GetType().Name}")
        };
    }

    private static object? EvaluateBinary(
        BinaryExpression expression,
        IReadOnlyDictionary<string, object?> payload)
    {
        if (expression.Operator is BinaryOperator.And or BinaryOperator.Or)
        {
            var leftBool = ToBoolean(EvaluateExpression(expression.Left, payload));
            var rightBool = ToBoolean(EvaluateExpression(expression.Right, payload));
            return expression.Operator == BinaryOperator.And ? leftBool && rightBool : leftBool || rightBool;
        }

        if (expression.Operator is BinaryOperator.Add or BinaryOperator.Subtract or BinaryOperator.Multiply or BinaryOperator.Divide or BinaryOperator.Modulo)
        {
            var leftNumber = ToNumber(EvaluateExpression(expression.Left, payload));
            var rightNumber = ToNumber(EvaluateExpression(expression.Right, payload));

            return expression.Operator switch
            {
                BinaryOperator.Add => leftNumber + rightNumber,
                BinaryOperator.Subtract => leftNumber - rightNumber,
                BinaryOperator.Multiply => leftNumber * rightNumber,
                BinaryOperator.Divide => rightNumber == 0 ? throw new ArgumentException("Division by zero.") : leftNumber / rightNumber,
                BinaryOperator.Modulo => rightNumber == 0 ? throw new ArgumentException("Modulo by zero.") : leftNumber % rightNumber,
                _ => throw new ArgumentException($"Unsupported arithmetic operator {expression.Operator}.")
            };
        }

        var left = EvaluateExpression(expression.Left, payload);
        var right = EvaluateExpression(expression.Right, payload);

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

    private static bool EvaluateUnary(UnaryExpression expression, IReadOnlyDictionary<string, object?> payload)
    {
        var operand = EvaluateExpression(expression.Operand, payload);
        return expression.Operator switch
        {
            UnaryOperator.Not => !ToBoolean(operand),
            UnaryOperator.IsNull => operand == null,
            UnaryOperator.IsNotNull => operand != null,
            _ => throw new ArgumentException($"Unsupported unary operator {expression.Operator}.")
        };
    }

    private static object? EvaluateFunction(FunctionCall function, IReadOnlyDictionary<string, object?> payload)
    {
        var name = function.FunctionName.ToUpperInvariant();
        var args = function.Arguments.Select(arg => EvaluateExpression(arg, payload)).ToArray();

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

    private static object? GetLayerPayloadPropertyValue(
        IReadOnlyDictionary<string, object?> payload,
        string propertyName)
    {
        if (payload.TryGetValue(propertyName, out var value))
        {
            return value;
        }

        throw new ArgumentException($"Unknown layer property '{propertyName}'.");
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

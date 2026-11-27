// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Queries.Filters;

public static class FilterStringAmplificationGuard
{
    public const int MaxDerivedStringLength = FilterParserGuard.MaxStringLiteralLength;
    public const int MaxStringReplacementDepth = 8;
    private const long AssumedDynamicStringLength = 256;

    public static void EnsureWithinBounds(FilterExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        _ = EstimateDerivedStringLength(expression, replaceDepth: 0);
    }

    private static long EstimateDerivedStringLength(FilterExpression expression, int replaceDepth)
    {
        return expression switch
        {
            Literal literal => EstimateLiteralLength(literal),
            PropertyReference => AssumedDynamicStringLength,
            FunctionCall function => EstimateFunctionLength(function, replaceDepth),
            _ => VisitChildren(expression, replaceDepth)
        };
    }

    private static long EstimateLiteralLength(Literal literal)
    {
        if (literal.Value is null)
        {
            return 0;
        }

        return literal.Value is string text
            ? text.Length
            : Convert.ToString(literal.Value, CultureInfo.InvariantCulture)?.Length ?? 0;
    }

    private static long EstimateFunctionLength(FunctionCall function, int replaceDepth)
    {
        var name = function.FunctionName.ToUpperInvariant();
        if (name == "CONCAT")
        {
            return EstimateConcatLength(function.Arguments, replaceDepth);
        }

        if (name == "REPLACE")
        {
            return EstimateReplaceLength(function.Arguments, replaceDepth + 1);
        }

        return EstimateOtherFunctionLength(name, function.Arguments, replaceDepth);
    }

    private static long EstimateConcatLength(IReadOnlyList<FilterExpression> arguments, int replaceDepth)
    {
        long total = 0;
        for (var i = 0; i < arguments.Count; i++)
        {
            total += EstimateDerivedStringLength(arguments[i], replaceDepth);
            if (total > MaxDerivedStringLength)
            {
                throw new ArgumentException(
                    $"Filter expression derived string exceeds the maximum length of {MaxDerivedStringLength} characters.");
            }
        }

        return total;
    }

    private static long EstimateReplaceLength(IReadOnlyList<FilterExpression> arguments, int replaceDepth)
    {
        if (replaceDepth > MaxStringReplacementDepth)
        {
            throw new ArgumentException(
                $"Filter expression exceeds the maximum string replacement nesting depth of {MaxStringReplacementDepth}.");
        }

        if (arguments.Count != 3)
        {
            return VisitArgumentList(arguments, replaceDepth);
        }

        var sourceLength = EstimateDerivedStringLength(arguments[0], replaceDepth);
        _ = EstimateDerivedStringLength(arguments[1], replaceDepth);
        var replacementLength = EstimateDerivedStringLength(arguments[2], replaceDepth);
        var targetLength = ResolveReplaceTargetLength(arguments[1]);
        var outputLength = ComputeWorstCaseReplaceLength(sourceLength, targetLength, replacementLength);

        if (outputLength > MaxDerivedStringLength)
        {
            throw new ArgumentException(
                $"Filter expression derived string exceeds the maximum length of {MaxDerivedStringLength} characters.");
        }

        return outputLength;
    }

    private static long ResolveReplaceTargetLength(FilterExpression target)
    {
        if (target is Literal { Value: string text })
        {
            return text.Length;
        }

        return target is Literal { Value: null } ? 0 : 1;
    }

    private static long ComputeWorstCaseReplaceLength(long sourceLength, long targetLength, long replacementLength)
    {
        if (targetLength <= 0 || replacementLength <= targetLength)
        {
            return sourceLength;
        }

        var maxOccurrences = sourceLength / targetLength;
        var delta = replacementLength - targetLength;
        if (maxOccurrences > (MaxDerivedStringLength - sourceLength) / delta)
        {
            return MaxDerivedStringLength + 1;
        }

        return sourceLength + (maxOccurrences * delta);
    }

    private static long EstimateOtherFunctionLength(
        string normalizedName,
        IReadOnlyList<FilterExpression> arguments,
        int replaceDepth)
    {
        long firstArgLength = 0;
        for (var i = 0; i < arguments.Count; i++)
        {
            var argLength = EstimateDerivedStringLength(arguments[i], replaceDepth);
            if (i == 0)
            {
                firstArgLength = argLength;
            }
        }

        return IsLengthPreservingStringFunction(normalizedName)
            ? firstArgLength
            : AssumedDynamicStringLength;
    }

    private static bool IsLengthPreservingStringFunction(string normalizedName)
        => normalizedName is "UPPER" or "LOWER" or "TRIM" or "LTRIM" or "RTRIM"
            or "SUBSTRING" or "SUBSTR" or "LEFT" or "RIGHT" or "CASEI" or "ACCENTI";

    private static long VisitArgumentList(IReadOnlyList<FilterExpression> arguments, int replaceDepth)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            _ = EstimateDerivedStringLength(arguments[i], replaceDepth);
        }

        return AssumedDynamicStringLength;
    }

    private static long VisitChildren(FilterExpression expression, int replaceDepth)
    {
        foreach (var child in FilterExpressionNormalizer.EnumerateChildren(expression))
        {
            _ = EstimateDerivedStringLength(child, replaceDepth);
        }

        return 0;
    }
}

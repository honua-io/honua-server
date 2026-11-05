// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Geoprocessing.Execution.Expressions;

internal static class ExpressionEngine
{
    public const int MaxExpressionLength = 4096;

    // The depth cap bounds parser and evaluator recursion so a nested expression cannot blow the stack.
    public const int MaxDepth = 64;

    public static ExpressionNode Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > MaxExpressionLength)
        {
            throw new ExpressionParseException(
                $"expression exceeds the maximum length of {MaxExpressionLength} characters");
        }

        var tokens = ExpressionLexer.Tokenize(source);
        var parser = new ExpressionParser(tokens);
        var tree = parser.ParseExpression();
        parser.ExpectEnd();
        return tree;
    }

    public static object? Evaluate(string source, IExpressionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Parse(source).Evaluate(context);
    }

    public static string ToInvariantString(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        bool b => b ? "true" : "false",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}

internal interface IExpressionContext
{
    bool TryGetField(string name, out object? value);
}

internal sealed class ExpressionParseException(string message) : Exception(message)
{
    public string PublicMessage { get; } = message;
}

// The message is sanitized before it is shown to callers.
internal sealed class ExpressionEvaluationException(string message) : Exception(message)
{
    public string PublicMessage { get; } = message;
}

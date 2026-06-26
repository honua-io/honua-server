// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Geoprocessing.Execution.Expressions;

/// <summary>
/// A safe, AOT-friendly expression engine for the GeoETL <c>transform.computed-field</c>
/// transform. The engine parses a source string into an immutable abstract syntax
/// tree (AST) over a fixed, whitelisted grammar — there is NO reflection, NO dynamic
/// code generation (Roslyn / Expression-tree compilation), and NO arbitrary member
/// access. Every operator and function is a hand-coded node, so the evaluator is
/// fully analyzable and trim/AOT-safe.
/// </summary>
/// <remarks>
/// <para>Grammar (Pratt / precedence-climbing parser):</para>
/// <code>
/// expr        := ternary
/// ternary     := logicalOr ( '?' expr ':' expr )?
/// logicalOr   := logicalAnd ( ( '||' | 'or' ) logicalAnd )*
/// logicalAnd  := equality   ( ( '&amp;&amp;' | 'and' ) equality )*
/// equality    := comparison ( ( '==' | '!=' ) comparison )*
/// comparison  := additive   ( ( '&lt;' | '&lt;=' | '&gt;' | '&gt;=' ) additive )*
/// additive    := multiplicative ( ( '+' | '-' ) multiplicative )*
/// multiplicative := unary ( ( '*' | '/' | '%' ) unary )*
/// unary       := ( '-' | '!' | 'not' ) unary | primary
/// primary     := NUMBER | STRING | 'true' | 'false' | 'null'
///              | IDENT                       (field reference)
///              | IDENT '(' args? ')'         (function call)
///              | '(' expr ')'
/// </code>
/// <para>
/// The function set is whitelisted in <see cref="ExpressionFunctions"/>. Field
/// references resolve through an <see cref="IExpressionContext"/> supplied at
/// evaluation time; an unknown function or an attempt to parse anything outside
/// the grammar is rejected at <em>parse</em> time with an
/// <see cref="ExpressionParseException"/>, so unsafe input never reaches evaluation.
/// </para>
/// </remarks>
internal static class ExpressionEngine
{
    /// <summary>
    /// Maximum source length accepted by <see cref="Parse"/>. Bounds parser work
    /// and guards against pathological inputs; computed-field expressions are short.
    /// </summary>
    public const int MaxExpressionLength = 4096;

    /// <summary>
    /// Maximum AST depth. Bounds recursion in both the parser and the evaluator so
    /// a deeply nested expression cannot blow the stack.
    /// </summary>
    public const int MaxDepth = 64;

    /// <summary>
    /// Parses <paramref name="source"/> into an immutable expression tree. Throws
    /// <see cref="ExpressionParseException"/> for any syntax error, unknown
    /// function, or disallowed token — the single safety gate. The returned tree is
    /// thread-safe and may be cached and evaluated repeatedly.
    /// </summary>
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

    /// <summary>
    /// Convenience parse-and-evaluate over a single context. Prefer
    /// <see cref="Parse"/> + repeated <see cref="ExpressionNode.Evaluate"/> when the
    /// same expression runs over many features.
    /// </summary>
    public static object? Evaluate(string source, IExpressionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Parse(source).Evaluate(context);
    }

    /// <summary>
    /// Formats an arbitrary evaluator value as an invariant string, matching the
    /// coercion used by the <c>string(...)</c> / <c>cast(..., string)</c> functions.
    /// </summary>
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

/// <summary>
/// Supplies field-reference values to the expression evaluator. Implementations
/// project a feature's attribute table (or any other named-value source) without
/// exposing reflection or arbitrary members to the grammar.
/// </summary>
internal interface IExpressionContext
{
    /// <summary>
    /// Resolves a field reference. Returns <c>true</c> and the value when the field
    /// exists (the value may itself be <c>null</c>); returns <c>false</c> when the
    /// field is absent so the evaluator can treat it as <c>null</c>.
    /// </summary>
    bool TryGetField(string name, out object? value);
}

/// <summary>
/// Thrown for any syntactic or semantic error encountered while parsing an
/// expression. Carries a public, sanitized message safe to surface to callers.
/// </summary>
internal sealed class ExpressionParseException(string message) : Exception(message)
{
    /// <summary>The sanitized, caller-safe error message.</summary>
    public string PublicMessage { get; } = message;
}

/// <summary>
/// Thrown for a runtime evaluation error (e.g. a type the grammar cannot coerce).
/// Carries a public, sanitized message safe to surface to callers.
/// </summary>
internal sealed class ExpressionEvaluationException(string message) : Exception(message)
{
    /// <summary>The sanitized, caller-safe error message.</summary>
    public string PublicMessage { get; } = message;
}

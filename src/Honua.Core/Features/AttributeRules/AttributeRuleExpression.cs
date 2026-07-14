// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.AttributeRules;

/// <summary>
/// Outcome of evaluating an attribute-rule expression.
/// </summary>
/// <param name="Status">Whether the expression produced a value, reported a constraint violation, or was unsupported.</param>
/// <param name="Value">
/// The computed value for a successful calculation expression. <c>null</c> for boolean
/// constraint evaluations and for unsupported/skipped expressions.
/// </param>
public readonly record struct AttributeRuleExpressionResult(
    AttributeRuleExpressionStatus Status,
    object? Value)
{
    /// <summary>Creates a result for a successfully computed value (calculation).</summary>
    /// <param name="value">The computed value.</param>
    /// <returns>A success result carrying <paramref name="value"/>.</returns>
    public static AttributeRuleExpressionResult Computed(object? value)
        => new(AttributeRuleExpressionStatus.Computed, value);

    /// <summary>Creates a result for a boolean constraint that passed.</summary>
    public static AttributeRuleExpressionResult Satisfied { get; } =
        new(AttributeRuleExpressionStatus.Satisfied, true);

    /// <summary>Creates a result for a boolean constraint that was violated.</summary>
    public static AttributeRuleExpressionResult Violated { get; } =
        new(AttributeRuleExpressionStatus.Violated, false);

    /// <summary>
    /// Creates a result for an expression outside the supported safe subset. The caller
    /// routes these out of scope (skips with a logged warning) rather than failing the edit.
    /// </summary>
    public static AttributeRuleExpressionResult Unsupported { get; } =
        new(AttributeRuleExpressionStatus.Unsupported, null);
}

/// <summary>
/// Status of an evaluated attribute-rule expression.
/// </summary>
public enum AttributeRuleExpressionStatus
{
    /// <summary>The calculation expression produced a value (carried in the result).</summary>
    Computed,

    /// <summary>The boolean constraint expression evaluated to true (no violation).</summary>
    Satisfied,

    /// <summary>The boolean constraint expression evaluated to false (violation).</summary>
    Violated,

    /// <summary>
    /// The expression is outside the supported safe subset and must be routed out of
    /// scope (skipped) rather than enforced.
    /// </summary>
    Unsupported
}

/// <summary>
/// Evaluates the deliberately small, safe subset of Arcade that Honua enforces on the
/// edit path for attribute rules. The evaluator is parameterized over a feature's
/// attributes only; it performs no I/O, no reflection, no SQL, and no arbitrary code
/// execution. Expressions outside the subset return
/// <see cref="AttributeRuleExpressionStatus.Unsupported"/> so callers can route them to
/// an out-of-scope path (AI resolver / skip-with-warning) without failing the edit.
/// <para>
/// Supported grammar (whitespace-insensitive):
/// <list type="bullet">
///   <item>Literals: numbers (<c>42</c>, <c>3.5</c>), single/double-quoted strings, <c>true</c>/<c>false</c>/<c>null</c>.</item>
///   <item>Field references: <c>$feature.FieldName</c> or <c>$feature["Field Name"]</c>.</item>
///   <item>Arithmetic over numbers: <c>+ - * /</c> (left-to-right, no precedence beyond grouping is required since callers use simple expressions; <c>*</c>/<c>/</c> bind tighter than <c>+</c>/<c>-</c>).</item>
///   <item>Comparisons: <c>== != &lt; &lt;= &gt; &gt;=</c>.</item>
///   <item>Boolean combination: <c>&amp;&amp;</c> and <c>||</c>.</item>
///   <item>Parentheses for grouping.</item>
///   <item>Optional trailing semicolon and a leading <c>return</c> keyword (Esri wraps calc scripts as <c>return &lt;expr&gt;;</c>).</item>
///   <item>String concatenation via <c>+</c> when either operand is a string.</item>
///   <item>
///     A documented allow-list of pure Arcade functions (case-insensitive):
///     <c>Upper</c>, <c>Lower</c>, <c>Trim</c>, <c>Text</c>, <c>Concatenate</c>,
///     <c>Round</c>, <c>Floor</c>, <c>Ceil</c>, <c>Abs</c>, <c>IsEmpty</c>.
///   </item>
/// </list>
/// Anything else — functions outside the allow-list, <c>if</c>/<c>var</c>/multi-statement
/// scripts, member access beyond <c>$feature</c>, etc. — is treated as unsupported and the
/// caller routes it out of scope rather than failing the edit.
/// </para>
/// </summary>
public static class AttributeRuleExpression
{
    /// <summary>
    /// Evaluates <paramref name="expression"/> against the supplied feature attributes.
    /// </summary>
    /// <param name="expression">The Arcade expression text.</param>
    /// <param name="attributes">Case-insensitive feature attribute lookup.</param>
    /// <param name="expectBoolean">
    /// True for constraint/validation rules (the result is interpreted as a pass/fail
    /// boolean); false for calculation rules (the result is the computed value).
    /// </param>
    /// <returns>The evaluation outcome.</returns>
    public static AttributeRuleExpressionResult Evaluate(
        string? expression,
        IReadOnlyDictionary<string, object?> attributes,
        bool expectBoolean)
    {
        ArgumentNullException.ThrowIfNull(attributes);

        if (string.IsNullOrWhiteSpace(expression))
        {
            return AttributeRuleExpressionResult.Unsupported;
        }

        var normalized = Normalize(expression);
        if (normalized is null)
        {
            return AttributeRuleExpressionResult.Unsupported;
        }

        var parser = new Parser(normalized, attributes);
        if (!parser.TryParse(out var value))
        {
            return AttributeRuleExpressionResult.Unsupported;
        }

        if (expectBoolean)
        {
            if (value is bool boolean)
            {
                return boolean ? AttributeRuleExpressionResult.Satisfied : AttributeRuleExpressionResult.Violated;
            }

            // A constraint expression that did not reduce to a boolean is not something we
            // can safely enforce; route it out of scope rather than guessing truthiness.
            return AttributeRuleExpressionResult.Unsupported;
        }

        return AttributeRuleExpressionResult.Computed(value);
    }

    // Strips an optional leading 'return' and a single trailing ';'. Rejects (returns null)
    // anything that still contains a ';' afterwards (multi-statement script) or other
    // tokens we don't support, leaving fine-grained rejection to the parser.
    private static string? Normalize(string expression)
    {
        var trimmed = expression.Trim();
        if (trimmed.StartsWith("return ", StringComparison.Ordinal) ||
            trimmed.StartsWith("return\t", StringComparison.Ordinal))
        {
            trimmed = trimmed["return".Length..].TrimStart();
        }

        if (trimmed.EndsWith(';'))
        {
            trimmed = trimmed[..^1].TrimEnd();
        }

        if (trimmed.Length == 0 || trimmed.Contains(';', StringComparison.Ordinal))
        {
            return null;
        }

        return trimmed;
    }

    // Recursive-descent parser/evaluator for the supported subset. Returns false from
    // TryParse for any token or construct outside the grammar.
    private ref struct Parser(string text, IReadOnlyDictionary<string, object?> attributes)
    {
        // Each '(' recurses ~6 frames deep (ParseOr -> ... -> ParsePrimary); without a cap a
        // pathological expression of a few thousand nested parens overflows the stack, which is
        // an uncatchable process crash. 32 levels is far beyond any legitimate rule expression.
        // Mirrors FilterParserGuard.EnsureExpressionDepth on the filter parsers.
        private const int MaxGroupingDepth = 32;

        // Threshold for treating a division's right-hand operand as zero.
        // Deliberately far tighter than any realistic user-supplied divisor
        // (which legitimately spans everything down to very small fractions)
        // so real calculations keep working; it only catches an exact zero or
        // a near-zero result of upstream floating-point cancellation, which
        // would otherwise silently produce Infinity/NaN instead of Fail().
        private const double DivisionByZeroEpsilon = 1e-12;

        private readonly string _text = text;
        private readonly IReadOnlyDictionary<string, object?> _attributes = attributes;
        private int _pos;
        private int _depth;
        private bool _failed;

        public bool TryParse(out object? value)
        {
            value = ParseOr();
            SkipWhitespace();
            if (_failed || _pos != _text.Length)
            {
                value = null;
                return false;
            }

            return true;
        }

        private object? ParseOr()
        {
            var left = ParseAnd();
            while (!_failed && Match("||"))
            {
                var right = ParseAnd();
                left = AsBool(left) || AsBool(right);
            }

            return left;
        }

        private object? ParseAnd()
        {
            var left = ParseComparison();
            while (!_failed && Match("&&"))
            {
                var right = ParseComparison();
                left = AsBool(left) && AsBool(right);
            }

            return left;
        }

        private object? ParseComparison()
        {
            var left = ParseAdditive();
            SkipWhitespace();
            // Kept as a foreach rather than .Where()/.FirstOrDefault(): Match(op) mutates the
            // parser cursor (_pos) as a side effect of testing each operator, so this is a
            // stateful, order-dependent scan rather than a pure filter/projection.
            foreach (var op in ComparisonOperators)
            {
                if (Match(op))
                {
                    var right = ParseAdditive();
                    return Compare(left, right, op);
                }
            }

            return left;
        }

        private object? ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (!_failed)
            {
                SkipWhitespace();
                if (Match("+"))
                {
                    left = Arithmetic(left, ParseMultiplicative(), '+');
                }
                else if (Match("-"))
                {
                    left = Arithmetic(left, ParseMultiplicative(), '-');
                }
                else
                {
                    break;
                }
            }

            return left;
        }

        private object? ParseMultiplicative()
        {
            var left = ParsePrimary();
            while (!_failed)
            {
                SkipWhitespace();
                if (Match("*"))
                {
                    left = Arithmetic(left, ParsePrimary(), '*');
                }
                else if (Match("/"))
                {
                    left = Arithmetic(left, ParsePrimary(), '/');
                }
                else
                {
                    break;
                }
            }

            return left;
        }

        private object? ParsePrimary()
        {
            SkipWhitespace();
            if (_pos >= _text.Length)
            {
                return Fail();
            }

            var c = _text[_pos];
            if (c == '(')
            {
                if (++_depth > MaxGroupingDepth)
                {
                    return Fail();
                }

                _pos++;
                var inner = ParseOr();
                _depth--;
                SkipWhitespace();
                if (!Match(")"))
                {
                    return Fail();
                }

                return inner;
            }

            if (c is '\'' or '"')
            {
                return ParseString(c);
            }

            if (char.IsDigit(c) || (c == '-' && _pos + 1 < _text.Length && char.IsDigit(_text[_pos + 1])))
            {
                return ParseNumber();
            }

            if (c == '$')
            {
                return ParseFeatureReference();
            }

            if (char.IsLetter(c))
            {
                return ParseIdentifierOrCall();
            }

            return Fail();
        }

        private object? ParseIdentifierOrCall()
        {
            var start = _pos;
            while (_pos < _text.Length && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] == '_'))
            {
                _pos++;
            }

            var word = _text[start.._pos];

            // A bare identifier is only a literal keyword; anything else is a function call
            // (handled below) or unsupported.
            SkipWhitespace();
            if (_pos < _text.Length && _text[_pos] == '(')
            {
                return ParseCall(word);
            }

            return word switch
            {
                "true" => true,
                "false" => false,
                "null" => null,
                _ => Fail()
            };
        }

        private object? ParseCall(string name)
        {
            // Consume '('
            _pos++;
            var args = new List<object?>();
            SkipWhitespace();
            if (_pos < _text.Length && _text[_pos] == ')')
            {
                _pos++;
            }
            else
            {
                while (true)
                {
                    var arg = ParseOr();
                    if (_failed)
                    {
                        return null;
                    }

                    args.Add(arg);
                    SkipWhitespace();
                    if (Match(","))
                    {
                        continue;
                    }

                    if (Match(")"))
                    {
                        break;
                    }

                    return Fail();
                }
            }

            return InvokeFunction(name, args);
        }

        // Pure, allocation-light function allow-list. Unknown names or wrong argument shapes
        // route the whole expression out of scope (Fail) rather than throwing.
        private object? InvokeFunction(string name, List<object?> args)
        {
            switch (name.ToLowerInvariant())
            {
                case "upper":
                    return args.Count == 1 ? AsString(args[0]).ToUpperInvariant() : Fail();
                case "lower":
                    return args.Count == 1 ? AsString(args[0]).ToLowerInvariant() : Fail();
                case "trim":
                    return args.Count == 1 ? AsString(args[0]).Trim() : Fail();
                case "text":
                    return args.Count == 1 ? AsString(args[0]) : Fail();
                case "concatenate":
                    {
                        if (args.Count == 0)
                        {
                            return Fail();
                        }

                        var sb = new System.Text.StringBuilder();
                        foreach (var arg in args)
                        {
                            sb.Append(AsString(arg));
                        }

                        return sb.ToString();
                    }

                case "isempty":
                    return args.Count == 1
                        ? args[0] is null || (args[0] is string s && s.Length == 0)
                        : Fail();
                case "abs":
                    return args.Count == 1 && TryToDouble(args[0], out var a) ? Math.Abs(a) : Fail();
                case "floor":
                    return args.Count == 1 && TryToDouble(args[0], out var f) ? Math.Floor(f) : Fail();
                case "ceil":
                    return args.Count == 1 && TryToDouble(args[0], out var c) ? Math.Ceiling(c) : Fail();
                case "round":
                    {
                        if (args.Count == 1 && TryToDouble(args[0], out var r1))
                        {
                            return Math.Round(r1, MidpointRounding.AwayFromZero);
                        }

                        if (args.Count == 2 && TryToDouble(args[0], out var r2) && TryToDouble(args[1], out var digits))
                        {
                            var d = (int)digits;
                            if (d is < 0 or > 15)
                            {
                                return Fail();
                            }

                            return Math.Round(r2, d, MidpointRounding.AwayFromZero);
                        }

                        return Fail();
                    }

                default:
                    return Fail();
            }
        }

        private static string AsString(object? value)
            => value switch
            {
                null => string.Empty,
                string s => s,
                bool b => b ? "true" : "false",
                double d => d.ToString(CultureInfo.InvariantCulture),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            };

        private object? ParseFeatureReference()
        {
            // Consume "$feature"
            const string FeatureToken = "$feature";
            if (!Match(FeatureToken))
            {
                return Fail();
            }

            SkipWhitespace();
            string fieldName;
            if (_pos < _text.Length && _text[_pos] == '.')
            {
                _pos++;
                var start = _pos;
                while (_pos < _text.Length && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] == '_'))
                {
                    _pos++;
                }

                if (_pos == start)
                {
                    return Fail();
                }

                fieldName = _text[start.._pos];
            }
            else if (_pos < _text.Length && _text[_pos] == '[')
            {
                _pos++;
                SkipWhitespace();
                if (_pos >= _text.Length || (_text[_pos] != '\'' && _text[_pos] != '"'))
                {
                    return Fail();
                }

                var quote = _text[_pos];
                var name = ParseString(quote);
                if (_failed || name is not string nameString)
                {
                    return Fail();
                }

                SkipWhitespace();
                if (!Match("]"))
                {
                    return Fail();
                }

                fieldName = nameString;
            }
            else
            {
                return Fail();
            }

            return _attributes.TryGetValue(fieldName, out var fieldValue) ? fieldValue : null;
        }

        private object? ParseString(char quote)
        {
            _pos++; // opening quote
            var sb = new System.Text.StringBuilder();
            while (_pos < _text.Length)
            {
                var c = _text[_pos];
                if (c == quote)
                {
                    _pos++;
                    return sb.ToString();
                }

                // No escape-sequence support — a backslash means an unsupported expression.
                if (c == '\\')
                {
                    return Fail();
                }

                sb.Append(c);
                _pos++;
            }

            return Fail();
        }

        private object? ParseNumber()
        {
            var start = _pos;
            if (_text[_pos] == '-')
            {
                _pos++;
            }

            var hasDot = false;
            while (_pos < _text.Length)
            {
                var c = _text[_pos];
                if (char.IsDigit(c))
                {
                    _pos++;
                }
                else if (c == '.' && !hasDot)
                {
                    hasDot = true;
                    _pos++;
                }
                else
                {
                    break;
                }
            }

            var slice = _text[start.._pos];
            if (!double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                return Fail();
            }

            return number;
        }

        private object? Fail()
        {
            _failed = true;
            return null;
        }

        private bool Match(string token)
        {
            SkipWhitespace();
            if (_pos + token.Length > _text.Length)
            {
                return false;
            }

            if (string.CompareOrdinal(_text, _pos, token, 0, token.Length) != 0)
            {
                return false;
            }

            _pos += token.Length;
            return true;
        }

        private void SkipWhitespace()
        {
            while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
            {
                _pos++;
            }
        }

        private bool AsBool(object? value)
        {
            if (value is bool b)
            {
                return b;
            }

            // Non-bool operand: record the parse failure. The returned value is never
            // observed as a real result — TryParse() checks _failed first and discards
            // whatever ParseOr()/ParseAnd() computed — so any placeholder is fine here.
            Fail();
            return false;
        }

        private object? Arithmetic(object? left, object? right, char op)
        {
            if (_failed)
            {
                return null;
            }

            // String concatenation: '+' with a string operand joins their textual forms,
            // matching Arcade's overloaded '+'. Other operators require numeric operands.
            if (op == '+' && (left is string || right is string))
            {
                return AsString(left) + AsString(right);
            }

            if (!TryToDouble(left, out var l) || !TryToDouble(right, out var r))
            {
                return Fail();
            }

            return op switch
            {
                '+' => l + r,
                '-' => l - r,
                '*' => l * r,
                '/' => Math.Abs(r) <= DivisionByZeroEpsilon ? Fail() : l / r,
                _ => Fail()
            };
        }

        private object? Compare(object? left, object? right, string op)
        {
            if (_failed)
            {
                return null;
            }

            if (op is "==" or "!=")
            {
                var equal = ValuesEqual(left, right);
                return op == "==" ? equal : !equal;
            }

            if (!TryToDouble(left, out var l) || !TryToDouble(right, out var r))
            {
                return Fail();
            }

            return op switch
            {
                "<" => l < r,
                "<=" => l <= r,
                ">" => l > r,
                ">=" => l >= r,
                _ => Fail()
            };
        }

        private static bool ValuesEqual(object? left, object? right)
        {
            if (left is null || right is null)
            {
                return left is null && right is null;
            }

            if (TryToDouble(left, out var l) && TryToDouble(right, out var r))
            {
                return l.Equals(r);
            }

            if (left is bool lb && right is bool rb)
            {
                return lb == rb;
            }

            return string.Equals(
                Convert.ToString(left, CultureInfo.InvariantCulture),
                Convert.ToString(right, CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
        }

        private static bool TryToDouble(object? value, out double result)
        {
            switch (value)
            {
                case double d:
                    result = d;
                    return true;
                case float f:
                    result = f;
                    return true;
                case decimal m:
                    result = (double)m;
                    return true;
                case long l:
                    result = l;
                    return true;
                case int i:
                    result = i;
                    return true;
                case short s:
                    result = s;
                    return true;
                case byte b:
                    result = b;
                    return true;
                case string str when double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                    result = parsed;
                    return true;
                default:
                    result = 0;
                    return false;
            }
        }

        // Two-character comparison operators are matched before single-character ones so
        // ">=" is not mis-read as ">".
        private static readonly string[] ComparisonOperators = ["==", "!=", "<=", ">=", "<", ">"];
    }
}

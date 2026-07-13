// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Geoprocessing.Execution.Expressions;

/// <summary>
/// The whitelisted function set the expression grammar exposes. Every function is a
/// hand-coded switch arm — there is NO reflection, NO method binding, and NO way to
/// reach an arbitrary CLR member from an expression. A function the whitelist does
/// not contain is rejected at parse time, so unsafe identifiers never evaluate.
/// </summary>
/// <remarks>
/// Categories:
/// <list type="bullet">
/// <item>String: <c>concat</c>, <c>substr</c>, <c>upper</c>, <c>lower</c>, <c>trim</c>, <c>replace</c>, <c>length</c></item>
/// <item>Conditional: <c>if</c>, <c>coalesce</c></item>
/// <item>Math: <c>abs</c>, <c>round</c>, <c>floor</c>, <c>ceil</c>, <c>sqrt</c>, <c>pow</c>, <c>min</c>, <c>max</c></item>
/// <item>Cast/parse: <c>cast</c>, <c>number</c>, <c>string</c></item>
/// <item>Date: <c>now</c>, <c>year</c>, <c>month</c>, <c>day</c>, <c>parsedate</c></item>
/// </list>
/// </remarks>
internal static class ExpressionFunctions
{
    // Min/max arity per function (inclusive). -1 max means variadic.
    private static readonly IReadOnlyDictionary<string, (int Min, int Max)> Arities =
        new Dictionary<string, (int, int)>(StringComparer.Ordinal)
        {
            ["concat"] = (1, -1),
            ["substr"] = (2, 3),
            ["upper"] = (1, 1),
            ["lower"] = (1, 1),
            ["trim"] = (1, 1),
            ["replace"] = (3, 3),
            ["length"] = (1, 1),
            ["if"] = (3, 3),
            ["coalesce"] = (1, -1),
            ["abs"] = (1, 1),
            ["round"] = (1, 2),
            ["floor"] = (1, 1),
            ["ceil"] = (1, 1),
            ["sqrt"] = (1, 1),
            ["pow"] = (2, 2),
            ["min"] = (2, -1),
            ["max"] = (2, -1),
            ["cast"] = (2, 2),
            ["number"] = (1, 1),
            ["string"] = (1, 1),
            ["now"] = (0, 0),
            ["year"] = (1, 1),
            ["month"] = (1, 1),
            ["day"] = (1, 1),
            ["parsedate"] = (1, 1),
        };

    /// <summary>A human-readable list of allowed function names for error messages.</summary>
    public static string AllowedFunctionList { get; } =
        string.Join(", ", Arities.Keys.OrderBy(k => k, StringComparer.Ordinal));

    /// <summary>Returns <c>true</c> when <paramref name="name"/> (case-insensitive) is a whitelisted function.</summary>
    public static bool IsKnown(string name, out string lowered)
    {
        lowered = name.ToLowerInvariant();
        return Arities.ContainsKey(lowered);
    }

    /// <summary>Validates the supplied argument count against the function's arity.</summary>
    /// <exception cref="ExpressionParseException">When the count is out of range.</exception>
    public static void ValidateArity(string loweredName, int count)
    {
        var (min, max) = Arities[loweredName];
        if (count < min || (max >= 0 && count > max))
        {
            var expected = max < 0 ? $"at least {min}" : min == max ? $"{min}" : $"{min}–{max}";
            throw new ExpressionParseException(
                $"function '{loweredName}' expects {expected} argument(s) but got {count}");
        }
    }

    /// <summary>Evaluates a whitelisted function over its already-parsed argument nodes.</summary>
    public static object? Invoke(string loweredName, IReadOnlyList<ExpressionNode> args, IExpressionContext context)
    {
        switch (loweredName)
        {
            // ---- String -------------------------------------------------------
            case "concat":
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var arg in args)
                    {
                        sb.Append(ExpressionEngine.ToInvariantString(arg.Evaluate(context)));
                    }

                    return sb.ToString();
                }
            case "substr":
                {
                    var s = ExpressionEngine.ToInvariantString(args[0].Evaluate(context));
                    var start = (int)ExpressionValues.ToNumber(args[1].Evaluate(context));
                    start = Math.Clamp(start, 0, s.Length);
                    if (args.Count == 3)
                    {
                        var len = (int)ExpressionValues.ToNumber(args[2].Evaluate(context));
                        len = Math.Clamp(len, 0, s.Length - start);
                        return s.Substring(start, len);
                    }

                    return s[start..];
                }
            case "upper":
                return ExpressionEngine.ToInvariantString(args[0].Evaluate(context)).ToUpperInvariant();
            case "lower":
                return ExpressionEngine.ToInvariantString(args[0].Evaluate(context)).ToLowerInvariant();
            case "trim":
                return ExpressionEngine.ToInvariantString(args[0].Evaluate(context)).Trim();
            case "replace":
                {
                    var s = ExpressionEngine.ToInvariantString(args[0].Evaluate(context));
                    var from = ExpressionEngine.ToInvariantString(args[1].Evaluate(context));
                    var to = ExpressionEngine.ToInvariantString(args[2].Evaluate(context));
                    return from.Length == 0 ? s : s.Replace(from, to, StringComparison.Ordinal);
                }
            case "length":
                return (double)ExpressionEngine.ToInvariantString(args[0].Evaluate(context)).Length;

            // ---- Conditional --------------------------------------------------
            case "if":
                return ExpressionValues.ToBool(args[0].Evaluate(context))
                    ? args[1].Evaluate(context)
                    : args[2].Evaluate(context);
            case "coalesce":
                // Treat an empty string as "absent" so coalesce skips blank fields. Select
                // stays lazy through FirstOrDefault, so later args are only evaluated when
                // an earlier one is null/blank — matching the original loop's short circuit.
                return args
                    .Select(arg => arg.Evaluate(context))
                    .FirstOrDefault(value => value is not (null or string { Length: 0 }));

            // ---- Math ---------------------------------------------------------
            case "abs":
                return Math.Abs(ExpressionValues.ToNumber(args[0].Evaluate(context)));
            case "round":
                {
                    var value = ExpressionValues.ToNumber(args[0].Evaluate(context));
                    var digits = args.Count == 2 ? (int)ExpressionValues.ToNumber(args[1].Evaluate(context)) : 0;
                    digits = Math.Clamp(digits, 0, 15);
                    return Math.Round(value, digits, MidpointRounding.AwayFromZero);
                }
            case "floor":
                return Math.Floor(ExpressionValues.ToNumber(args[0].Evaluate(context)));
            case "ceil":
                return Math.Ceiling(ExpressionValues.ToNumber(args[0].Evaluate(context)));
            case "sqrt":
                {
                    var value = ExpressionValues.ToNumber(args[0].Evaluate(context));
                    if (value < 0)
                    {
                        throw new ExpressionEvaluationException("sqrt of a negative number");
                    }

                    return Math.Sqrt(value);
                }
            case "pow":
                return Math.Pow(
                    ExpressionValues.ToNumber(args[0].Evaluate(context)),
                    ExpressionValues.ToNumber(args[1].Evaluate(context)));
            case "min":
                {
                    var result = ExpressionValues.ToNumber(args[0].Evaluate(context));
                    for (var i = 1; i < args.Count; i++)
                    {
                        result = Math.Min(result, ExpressionValues.ToNumber(args[i].Evaluate(context)));
                    }

                    return result;
                }
            case "max":
                {
                    var result = ExpressionValues.ToNumber(args[0].Evaluate(context));
                    for (var i = 1; i < args.Count; i++)
                    {
                        result = Math.Max(result, ExpressionValues.ToNumber(args[i].Evaluate(context)));
                    }

                    return result;
                }

            // ---- Cast / parse -------------------------------------------------
            case "cast":
                {
                    var value = args[0].Evaluate(context);

                    // The target type may be written as a bare keyword — cast(year, string)
                    // — in which case the parser produced a FieldNode for the second
                    // argument. Use that identifier as the type name directly rather than
                    // resolving it as a (typically absent) field. A quoted string literal
                    // — cast(year, "string") — still works through normal evaluation.
                    var targetType = args[1] is FieldNode typeField
                        ? typeField.Name.Trim().ToLowerInvariant()
                        : ExpressionEngine.ToInvariantString(args[1].Evaluate(context)).Trim().ToLowerInvariant();
                    return Cast(value, targetType);
                }
            case "number":
                return ExpressionValues.ToNumber(args[0].Evaluate(context));
            case "string":
                return ExpressionEngine.ToInvariantString(args[0].Evaluate(context));

            // ---- Date ---------------------------------------------------------
            case "now":
                return DateTimeOffset.UtcNow;
            case "year":
                return (double)ToDate(args[0].Evaluate(context)).Year;
            case "month":
                return (double)ToDate(args[0].Evaluate(context)).Month;
            case "day":
                return (double)ToDate(args[0].Evaluate(context)).Day;
            case "parsedate":
                return ToDate(args[0].Evaluate(context));

            default:
                // Unreachable: parse-time validation rejects unknown names.
                throw new ExpressionEvaluationException($"function '{loweredName}' is not implemented");
        }
    }

    private static object? Cast(object? value, string targetType) => targetType switch
    {
        "int" or "integer" or "long" => Math.Truncate(ExpressionValues.ToNumber(value)),
        "double" or "float" or "number" or "decimal" => ExpressionValues.ToNumber(value),
        "string" or "text" => ExpressionEngine.ToInvariantString(value),
        "bool" or "boolean" => ExpressionValues.ToBool(value),
        _ => throw new ExpressionEvaluationException(
            $"cast target type '{targetType}' is not supported (allowed: int, long, double, string, bool)"),
    };

    private static DateTimeOffset ToDate(object? value) => value switch
    {
        DateTimeOffset dto => dto,
        DateTime dt => new DateTimeOffset(dt, TimeSpan.Zero),
        double ms => DateTimeOffset.FromUnixTimeMilliseconds((long)ms),
        string s when DateTimeOffset.TryParse(
            s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) => parsed,
        _ => throw new ExpressionEvaluationException($"value '{ExpressionEngine.ToInvariantString(value)}' is not a date"),
    };
}

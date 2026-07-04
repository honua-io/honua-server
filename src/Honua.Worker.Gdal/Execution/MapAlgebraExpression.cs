// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Strict allow-list validator for the user-supplied <c>raster.map-algebra</c>
/// expression (#2239). <c>gdal_calc.py</c> evaluates the <c>--calc</c> string with
/// Python <c>eval()</c> in a NumPy namespace, so an un-vetted expression is remote
/// code execution. This validator admits ONLY:
/// <list type="bullet">
/// <item>single-uppercase band variables A–Z, each within the supplied band count;</item>
/// <item>a fixed set of arithmetic / comparison / logical operators and grouping;</item>
/// <item>numeric literals; and</item>
/// <item>a small allow-list of safe NumPy element-wise functions.</item>
/// </list>
/// Underscores, quotes, attribute access, indexing, and any other character are
/// rejected, which blocks dunder / import / attribute-traversal escapes.
///
/// <para>
/// The same rule is mirrored at submit time in
/// <c>ProcessPlanValidator.ValidateRasterMapAlgebraSemantics</c> so a plan accepted
/// at submit is also accepted by the executor.
/// </para>
/// </summary>
internal static class MapAlgebraExpression
{
    /// <summary>Defensive upper bound on the expression length.</summary>
    public const int MaxLength = 2048;

    // Element-wise NumPy functions safe to expose in the calc namespace. No I/O,
    // attribute traversal, or object construction primitives are included.
    private static readonly FrozenSet<string> AllowedFunctions = new HashSet<string>(StringComparer.Ordinal)
    {
        "abs", "absolute", "minimum", "maximum", "sqrt", "exp", "log", "log10",
        "power", "where", "clip", "sign", "floor", "ceil", "mod", "fmod",
        "sin", "cos", "tan", "arctan", "nan_to_num",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Validates <paramref name="expression"/> against the allow-list for a calc
    /// with <paramref name="bandCount"/> source rasters (band variables A …
    /// A+bandCount-1).
    /// </summary>
    public static bool IsAllowed(string expression, int bandCount, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "'expression' must not be empty";
            return false;
        }

        var expr = expression.Trim();
        if (expr.Length > MaxLength)
        {
            error = $"'expression' exceeds the maximum length of {MaxLength} characters";
            return false;
        }

        var i = 0;
        var referencedAny = false;
        while (i < expr.Length)
        {
            var c = expr[i];

            if (char.IsWhiteSpace(c) || IsAllowedSymbol(c))
            {
                i++;
                continue;
            }

            if (char.IsDigit(c) || c == '.')
            {
                // Skip a numeric literal (digits, decimal point, optional exponent).
                while (i < expr.Length && (char.IsDigit(expr[i]) || expr[i] == '.'))
                {
                    i++;
                }

                // Consume an optional exponent ('e' or 'E') only when immediately followed
                // by at least one digit. A bare 'e' (e.g. "1e" or "1eA") is not a valid
                // Python literal and would produce a SyntaxError in gdal_calc.py.
                if (i < expr.Length && (expr[i] == 'e' || expr[i] == 'E'))
                {
                    var exponentStart = i;
                    i++; // advance past 'e'/'E'
                    if (i >= expr.Length || !char.IsDigit(expr[i]))
                    {
                        // No digit follows the exponent character — reject the expression.
                        error = $"'expression' contains an incomplete numeric exponent near position {exponentStart}";
                        return false;
                    }
                    while (i < expr.Length && char.IsDigit(expr[i]))
                    {
                        i++;
                    }
                }

                continue;
            }

            if (char.IsAsciiLetter(c))
            {
                var start = i;
                while (i < expr.Length && (char.IsAsciiLetterOrDigit(expr[i])))
                {
                    i++;
                }

                var identifier = expr[start..i];
                if (identifier.Length == 1 && identifier[0] is >= 'A' and <= 'Z')
                {
                    var bandIndex = identifier[0] - 'A';
                    if (bandIndex >= bandCount)
                    {
                        error = $"'expression' references band variable '{identifier}' but only {bandCount} source raster(s) were supplied";
                        return false;
                    }
                    referencedAny = true;
                    continue;
                }

                if (AllowedFunctions.Contains(identifier))
                {
                    continue;
                }

                error = $"'expression' contains a disallowed identifier '{identifier}'";
                return false;
            }

            error = $"'expression' contains a disallowed character '{c}'";
            return false;
        }

        if (!referencedAny)
        {
            error = "'expression' must reference at least one source band variable (A, B, …)";
            return false;
        }

        return true;
    }

    private static bool IsAllowedSymbol(char c) => c switch
    {
        '+' or '-' or '*' or '/' or '%' or '(' or ')' or ',' or '.' => true,
        '<' or '>' or '=' or '!' or '&' or '|' or '^' or '~' => true,
        _ => false,
    };
}

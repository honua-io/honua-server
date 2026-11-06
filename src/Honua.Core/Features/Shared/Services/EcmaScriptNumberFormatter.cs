// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.Shared.Services;

// ECMAScript Number::toString (ECMA-262 6.1.6.1.20). .NET "R" differs in exponent form (1E-07 vs 1e-7) and in
// when it switches to exponential notation, and JavaScript clients compare against this exact text.
public static class EcmaScriptNumberFormatter
{
    private const int MaxFixedNotationExponent = 21;
    private const int MinFixedNotationExponent = -6;

    public static string Format(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "Infinity" : "-Infinity";
        }

        // codeql[cs/equality-on-floats]: exact zero, including negative zero, is the ECMAScript "0" spelling
        if (value == 0d)
        {
            return "0";
        }

        return value < 0d ? "-" + FormatPositive(-value) : FormatPositive(value);
    }

    private static string FormatPositive(double value)
    {
        // "R" yields the shortest round-trippable digits, the digit sequence ECMAScript selects.
        var roundTrip = value.ToString("R", CultureInfo.InvariantCulture);
        var exponent = 0;
        var exponentIndex = roundTrip.IndexOf('E', StringComparison.Ordinal);
        if (exponentIndex >= 0)
        {
            exponent = int.Parse(roundTrip.AsSpan(exponentIndex + 1), NumberStyles.Integer, CultureInfo.InvariantCulture);
            roundTrip = roundTrip[..exponentIndex];
        }

        var pointIndex = roundTrip.IndexOf('.', StringComparison.Ordinal);
        var digits = pointIndex >= 0 ? roundTrip.Remove(pointIndex, 1) : roundTrip;
        var decimalPointPosition = (pointIndex >= 0 ? pointIndex : roundTrip.Length) + exponent;

        var leadingZeros = 0;
        while (leadingZeros < digits.Length - 1 && digits[leadingZeros] == '0')
        {
            leadingZeros++;
            decimalPointPosition--;
        }

        digits = digits[leadingZeros..].TrimEnd('0');
        return digits.Length == 0 ? "0" : Layout(digits, decimalPointPosition);
    }

    private static string Layout(string digits, int n)
    {
        var k = digits.Length;
        if (k <= n && n <= MaxFixedNotationExponent)
        {
            return digits + new string('0', n - k);
        }

        if (n > 0 && n <= MaxFixedNotationExponent)
        {
            return string.Concat(digits[..n], ".", digits[n..]);
        }

        if (n > MinFixedNotationExponent && n <= 0)
        {
            return string.Concat("0.", new string('0', -n), digits);
        }

        var mantissa = k == 1 ? digits : string.Concat(digits[..1], ".", digits[1..]);
        var scientificExponent = n - 1;
        return string.Concat(
            mantissa,
            "e",
            scientificExponent >= 0 ? "+" : "-",
            Math.Abs(scientificExponent).ToString(CultureInfo.InvariantCulture));
    }
}

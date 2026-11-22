// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Edit;

public static partial class FeatureAttributeValidator
{
    private static long? ReadInt64(object value) => value switch
    {
        sbyte number => number,
        byte number => number,
        short number => number,
        ushort number => number,
        int number => number,
        uint number => number,
        long number => number,
        ulong number when number <= long.MaxValue => (long)number,
        float number when IsWholeNumber(number) => (long)number,
        double number when IsWholeNumber(number) => (long)number,
        decimal number when decimal.Truncate(number) == number &&
            number >= long.MinValue && number <= long.MaxValue => (long)number,
        _ => null,
    };

    private static double? ReadDouble(object value) => value switch
    {
        float number => number,
        double number => number,
        decimal number => (double)number,
        sbyte number => number,
        byte number => number,
        short number => number,
        ushort number => number,
        int number => number,
        uint number => number,
        long number => number,
        ulong number => number,
        _ => null,
    };

    private static bool? ReadNumericBoolean(object value)
    {
        if (ReadInt64(value) is { } integer)
        {
            return integer is 0 or 1 ? integer == 1 : null;
        }
        if (ReadDouble(value) is { } number)
        {
            return number switch { 0 => false, 1 => true, _ => null };
        }
        return null;
    }

    private static bool IsWholeNumber(double value)
    {
        return !double.IsNaN(value) &&
               !double.IsInfinity(value) &&
               Math.Abs(value % 1) < double.Epsilon;
    }

    private static bool IsNumeric(object value)
    {
        return value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;
    }

    private static bool TryValidateBase64(string value)
    {
        try
        {
            Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

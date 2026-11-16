// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Edit;

public static partial class FeatureAttributeValidator
{
    private static bool TryGetInt64(object value, out long result)
    {
        switch (value)
        {
            case sbyte sbyteValue:
                result = sbyteValue;
                return true;
            case byte byteValue:
                result = byteValue;
                return true;
            case short shortValue:
                result = shortValue;
                return true;
            case ushort ushortValue:
                result = ushortValue;
                return true;
            case int intValue:
                result = intValue;
                return true;
            case uint uintValue:
                result = uintValue;
                return true;
            case long longValue:
                result = longValue;
                return true;
            case ulong ulongValue when ulongValue <= long.MaxValue:
                result = (long)ulongValue;
                return true;
            case float floatValue when IsWholeNumber(floatValue):
                result = (long)floatValue;
                return true;
            case double doubleValue when IsWholeNumber(doubleValue):
                result = (long)doubleValue;
                return true;
            case decimal decimalValue when decimal.Truncate(decimalValue) == decimalValue &&
                                           decimalValue >= long.MinValue &&
                                           decimalValue <= long.MaxValue:
                result = (long)decimalValue;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static bool TryGetDouble(object value, out double result)
    {
        switch (value)
        {
            case float floatValue:
                result = floatValue;
                return true;
            case double doubleValue:
                result = doubleValue;
                return true;
            case decimal decimalValue:
                result = (double)decimalValue;
                return true;
            case sbyte sbyteValue:
                result = sbyteValue;
                return true;
            case byte byteValue:
                result = byteValue;
                return true;
            case short shortValue:
                result = shortValue;
                return true;
            case ushort ushortValue:
                result = ushortValue;
                return true;
            case int intValue:
                result = intValue;
                return true;
            case uint uintValue:
                result = uintValue;
                return true;
            case long longValue:
                result = longValue;
                return true;
            case ulong ulongValue:
                result = ulongValue;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static bool TryGetBooleanFromNumeric(object value, out bool booleanValue)
    {
        if (TryGetInt64(value, out var longValue))
        {
            booleanValue = longValue == 1;
            return longValue is 0 or 1;
        }
        if (TryGetDouble(value, out var doubleValue))
        {
            booleanValue = doubleValue == 1;
            return doubleValue is 0 or 1;
        }
        booleanValue = false;
        return false;
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

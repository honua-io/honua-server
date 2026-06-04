// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;

namespace Honua.Protocols.GeoServices.FeatureServer;

internal static class FeatureServerValueParser
{
    public static bool TryConvertToLong(object? value, out long result)
    {
        result = 0;

        if (value == null)
        {
            return false;
        }

        switch (value)
        {
            case long longValue:
                result = longValue;
                return true;
            case int intValue:
                result = intValue;
                return true;
            case short shortValue:
                result = shortValue;
                return true;
            case byte byteValue:
                result = byteValue;
                return true;
            case uint uintValue:
                result = uintValue;
                return true;
            case ulong ulongValue when ulongValue <= long.MaxValue:
                result = (long)ulongValue;
                return true;
            case string stringValue:
                return long.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
            case JsonElement element:
                if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var elementLong))
                {
                    result = elementLong;
                    return true;
                }

                if (element.ValueKind == JsonValueKind.String)
                {
                    return long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
                }

                return false;
            default:
                return long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }
    }

    public static bool TryConvertToDouble(object? value, out double result)
    {
        result = 0;

        if (value == null)
        {
            return false;
        }

        switch (value)
        {
            case double doubleValue:
                result = doubleValue;
                return true;
            case float floatValue:
                result = floatValue;
                return true;
            case decimal decimalValue:
                result = (double)decimalValue;
                return true;
            case long longValue:
                result = longValue;
                return true;
            case int intValue:
                result = intValue;
                return true;
            case short shortValue:
                result = shortValue;
                return true;
            case byte byteValue:
                result = byteValue;
                return true;
            case uint uintValue:
                result = uintValue;
                return true;
            case ulong ulongValue:
                result = ulongValue;
                return true;
            case string stringValue:
                return double.TryParse(stringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
            case JsonElement element:
                if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var elementDouble))
                {
                    result = elementDouble;
                    return true;
                }

                if (element.ValueKind == JsonValueKind.String)
                {
                    return double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
                }

                return false;
            default:
                return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        }
    }
}

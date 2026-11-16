// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation;

namespace Honua.Core.Features.Edit;

public static partial class FeatureAttributeValidator
{
    private static string? ValidateAgainstDomain(
        MetadataV2Field field,
        object value,
        MetadataV2FieldDomain domain)
    {
        if (string.Equals(domain.Type, "codedValue", StringComparison.OrdinalIgnoreCase))
        {
            if (domain.CodedValues.Count == 0)
            {
                return null;
            }
            if (domain.CodedValues.Any(coded => CodedValueMatches(coded.Code, value)))
            {
                return null;
            }
            return $"Field '{field.Name}' value is not in the coded-value domain.";
        }

        return string.Equals(domain.Type, "range", StringComparison.OrdinalIgnoreCase)
            ? ValidateRange(field, value, domain) : null;
    }

    private static string? ValidateRange(MetadataV2Field field, object value, MetadataV2FieldDomain domain)
    {
        if (domain.Range is null || domain.Range.Count != 2)
        {
            return null;
        }
        if (field.Type == MetadataV2FieldType.Decimal && value is string exactText)
        {
            var numericDecimal = ExactAttributeValues.ParseDecimal(exactText);
            if (!TryReadDecimalBound(domain.Range[0], out var decimalMin) ||
                !TryReadDecimalBound(domain.Range[1], out var decimalMax))
            {
                return $"Field '{field.Name}' has unsupported decimal range bounds.";
            }
            return numericDecimal < decimalMin || numericDecimal > decimalMax
                ? $"Field '{field.Name}' value is outside the allowed range."
                : null;
        }
        if (!TryGetDouble(value, out var numeric))
        {
            // Non-numeric value against a numeric range — type validation
            // already covers the canonical-type case; let it pass.
            return null;
        }
        if (!TryReadRangeBound(domain.Range[0], out var min) ||
            !TryReadRangeBound(domain.Range[1], out var max))
        {
            return null;
        }
        if (numeric < min || numeric > max)
        {
            return $"Field '{field.Name}' value {numeric} is outside the allowed range [{min}, {max}].";
        }
        return null;
    }

    private static bool TryReadDecimalBound(JsonElement bound, out decimal value)
    {
        if (bound.ValueKind == JsonValueKind.Number)
        {
            return bound.TryGetDecimal(out value);
        }
        if (bound.ValueKind == JsonValueKind.String)
        {
            try
            {
                value = ExactAttributeValues.ParseDecimal(bound.GetString()!);
                return true;
            }
            catch (Exception exception) when (exception is ArgumentException or ExactValueRangeException)
            {
                // Invalid bounds are ignored, matching unsupported range metadata below.
            }
        }
        value = default;
        return false;
    }

    private static bool CodedValueMatches(JsonElement code, object value)
    {
        switch (code.ValueKind)
        {
            case JsonValueKind.String:
                {
                    var coded = code.GetString();
                    if (value is string s)
                    {
                        return string.Equals(coded, s, StringComparison.Ordinal);
                    }
                    return string.Equals(coded, value.ToString(), StringComparison.Ordinal);
                }
            case JsonValueKind.Number:
                if (code.TryGetInt64(out var codedLong) && TryGetInt64(value, out var valueLong))
                {
                    return codedLong == valueLong;
                }
                if (code.TryGetDouble(out var codedDouble) && TryGetDouble(value, out var valueDouble))
                {
                    return Math.Abs(codedDouble - valueDouble) < double.Epsilon;
                }
                return false;
            case JsonValueKind.True:
                return value is bool b && b;
            case JsonValueKind.False:
                return value is bool fb && !fb;
            case JsonValueKind.Null:
                return false;
            default:
                return false;
        }
    }

    private static bool TryReadRangeBound(JsonElement bound, out double value)
    {
        if (bound.ValueKind == JsonValueKind.Number && bound.TryGetDouble(out value))
        {
            return true;
        }
        if (bound.ValueKind == JsonValueKind.String &&
            double.TryParse(bound.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }
        value = 0;
        return false;
    }

}

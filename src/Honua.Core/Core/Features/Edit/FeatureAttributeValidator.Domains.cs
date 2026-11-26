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
        CompiledResourceFieldIndex index)
    {
        if (field.Domain is not { } domain)
        {
            return null;
        }

        if (string.Equals(domain.Type, "codedValue", StringComparison.OrdinalIgnoreCase))
        {
            if (domain.CodedValues.Count == 0)
            {
                return null;
            }

            if (index.CodedValueDomainsByFieldName is not null &&
                index.CodedValueDomainsByFieldName.TryGetValue(field.Name, out var compiled) &&
                compiled.Matches(value))
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
            if (ReadDecimalBound(domain.Range[0]) is not { } decimalMin ||
                ReadDecimalBound(domain.Range[1]) is not { } decimalMax)
            {
                return $"Field '{field.Name}' has unsupported decimal range bounds.";
            }
            return numericDecimal < decimalMin || numericDecimal > decimalMax
                ? $"Field '{field.Name}' value is outside the allowed range."
                : null;
        }
        if (ReadDouble(value) is not { } numeric)
        {
            // Non-numeric value against a numeric range — type validation
            // already covers the canonical-type case; let it pass.
            return null;
        }
        if (ReadRangeBound(domain.Range[0]) is not { } min ||
            ReadRangeBound(domain.Range[1]) is not { } max)
        {
            return null;
        }
        if (numeric < min || numeric > max)
        {
            return $"Field '{field.Name}' value {numeric} is outside the allowed range [{min}, {max}].";
        }
        return null;
    }

    private static decimal? ReadDecimalBound(JsonElement bound)
    {
        if (bound.ValueKind == JsonValueKind.Number)
        {
            return bound.TryGetDecimal(out var value) ? value : null;
        }
        if (bound.ValueKind == JsonValueKind.String)
        {
            try
            {
                return ExactAttributeValues.ParseDecimal(bound.GetString()!);
            }
            catch (Exception exception) when (exception is ArgumentException or ExactValueRangeException)
            {
                // The caller rejects unsupported decimal range metadata.
            }
        }
        return null;
    }

    private static double? ReadRangeBound(JsonElement bound)
    {
        if (bound.ValueKind == JsonValueKind.Number && bound.TryGetDouble(out var numeric))
        {
            return numeric;
        }
        if (bound.ValueKind == JsonValueKind.String &&
            double.TryParse(bound.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var textValue))
        {
            return textValue;
        }
        return null;
    }

}

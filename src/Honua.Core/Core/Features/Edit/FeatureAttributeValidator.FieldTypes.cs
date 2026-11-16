// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation;

namespace Honua.Core.Features.Edit;

public static partial class FeatureAttributeValidator
{
    private static string? ValidateAgainstFieldType(
        MetadataV2Field field, object value, AttributeValidationProfile profile) => field.Type switch
        {
            MetadataV2FieldType.String => ValidateString(field, value),
            MetadataV2FieldType.Integer => ValidateInteger(field, value),
            MetadataV2FieldType.BigInteger => TryGetInt64(value, out _)
                ? null : $"Field '{field.Name}' must be a 64-bit integer.",
            MetadataV2FieldType.Double or MetadataV2FieldType.Float => TryGetDouble(value, out _)
                ? null : $"Field '{field.Name}' must be a number.",
            MetadataV2FieldType.Decimal or MetadataV2FieldType.TimestampOffset => ValidateExact(field, value),
            MetadataV2FieldType.Boolean => value is bool ||
                (profile == AttributeValidationProfile.GeoServices && TryGetBooleanFromNumeric(value, out _))
                ? null : $"Field '{field.Name}' must be a boolean.",
            MetadataV2FieldType.DateTime => ValidateDateTimeValue(field.Name, value, profile),
            MetadataV2FieldType.Date => ValidateDateValue(field.Name, value, profile),
            MetadataV2FieldType.Time => ValidateTimeValue(field.Name, value),
            MetadataV2FieldType.Binary => ValidateBinary(field, value),
            MetadataV2FieldType.Uuid => value is Guid || (value is string text && Guid.TryParse(text, out _))
                ? null : $"Field '{field.Name}' must be a UUID.",
            // Geometry uses the dedicated geometry pipeline; JSON accepts normalized values.
            _ => null
        };

    private static string? ValidateString(MetadataV2Field field, object value)
    {
        if (value is not string text)
        {
            return $"Field '{field.Name}' must be a string.";
        }
        return field.Length.HasValue && text.Length > field.Length.Value
            ? $"Field '{field.Name}' exceeds maximum length of {field.Length.Value}." : null;
    }

    private static string? ValidateInteger(MetadataV2Field field, object value)
    {
        if (!TryGetInt64(value, out var number))
        {
            return $"Field '{field.Name}' must be an integer.";
        }
        return number < int.MinValue || number > int.MaxValue
            ? $"Field '{field.Name}' must be within 32-bit integer range." : null;
    }

    private static string? ValidateExact(MetadataV2Field field, object value)
    {
        if (value is not string text)
        {
            return $"Field '{field.Name}' must be an exact value encoded as text.";
        }
        try
        {
            if (field.Type == MetadataV2FieldType.Decimal)
            {
                _ = ExactAttributeValues.ParseDecimal(text);
            }
            else
            {
                _ = ExactAttributeValues.ParseTimestampOffset(text);
            }
            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or ExactValueRangeException)
        {
            return $"Field '{field.Name}' contains an invalid or unsupported exact value.";
        }
    }

    private static string? ValidateBinary(MetadataV2Field field, object value)
    {
        if (value is byte[])
        {
            return null;
        }
        if (value is string text)
        {
            return TryValidateBase64(text) ? null : $"Field '{field.Name}' must be a valid Base64 string.";
        }
        return $"Field '{field.Name}' must be binary data.";
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.Edit;

public static partial class FeatureAttributeValidator
{
    private static string? ValidateDateTimeValue(string fieldName, object value, AttributeValidationProfile profile)
    {
        if (value is DateTimeOffset || value is DateTime)
        {
            return null;
        }
        if (value is string stringValue)
        {
            return DateTimeOffset.TryParse(
                stringValue,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out _)
                ? null
                : $"Field '{fieldName}' must be a valid date-time string.";
        }
        if (profile == AttributeValidationProfile.GeoServices && IsNumeric(value))
        {
            return null;
        }
        return $"Field '{fieldName}' must be a valid date-time value.";
    }

    private static string? ValidateDateValue(string fieldName, object value, AttributeValidationProfile profile)
    {
        if (value is DateOnly || value is DateTimeOffset || value is DateTime)
        {
            return null;
        }
        if (value is string stringValue)
        {
            if (DateOnly.TryParse(stringValue, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                return null;
            }
            if (DateTimeOffset.TryParse(
                stringValue,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out _))
            {
                return null;
            }
            return $"Field '{fieldName}' must be a valid date string.";
        }
        if (profile == AttributeValidationProfile.GeoServices && IsNumeric(value))
        {
            return null;
        }
        return $"Field '{fieldName}' must be a valid date value.";
    }

    private static string? ValidateTimeValue(string fieldName, object value)
    {
        if (value is TimeOnly || value is TimeSpan)
        {
            return null;
        }
        if (value is string stringValue)
        {
            if (TimeOnly.TryParse(stringValue, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                return null;
            }
            if (TimeSpan.TryParse(stringValue, CultureInfo.InvariantCulture, out _))
            {
                return null;
            }
            return $"Field '{fieldName}' must be a valid time string.";
        }
        return $"Field '{fieldName}' must be a valid time value.";
    }

}

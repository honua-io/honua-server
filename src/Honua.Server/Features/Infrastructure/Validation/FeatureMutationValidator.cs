// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation;

namespace Honua.Server.Features.Infrastructure.Validation;

internal sealed class FeatureMutationValidator
{
    private readonly IGeometryValidator _geometryValidator;

    public FeatureMutationValidator(IGeometryValidator geometryValidator)
    {
        _geometryValidator = geometryValidator ?? throw new ArgumentNullException(nameof(geometryValidator));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Kept as an instance method for the validator service API.")]
    public ValidationResult<ImmutableDictionary<string, object?>> ValidateAttributes(
        MetadataV2Resource resource,
        IReadOnlyDictionary<string, object?>? attributes,
        ValidationExtensions.AttributeValidationMode mode,
        bool isUpdate = false)
    {
        return resource.ValidateAttributesV2(attributes, mode, isUpdate);
    }

    public async Task<GeometryMutationResult> ValidateGeometryAsync(
        byte[]? geometry,
        CancellationToken cancellationToken)
    {
        if (geometry == null || geometry.Length == 0)
        {
            return GeometryMutationResult.Success(geometry);
        }

        var validationResult = await _geometryValidator.ValidateCompleteAsync(geometry, cancellationToken);
        if (!validationResult.IsValid)
        {
            var errorMessages = string.Join("; ", validationResult.Errors.Select(error => error.Message));
            return GeometryMutationResult.Failure(errorMessages);
        }

        var finalGeometry = validationResult.WasRepaired
            ? validationResult.RepairedWkb
            : geometry;

        return GeometryMutationResult.Success(finalGeometry);
    }
}

internal sealed record GeometryMutationResult
{
    public bool IsValid { get; init; }
    public byte[]? Geometry { get; init; }
    public string? ErrorMessage { get; init; }

    public static GeometryMutationResult Success(byte[]? geometry)
        => new() { IsValid = true, Geometry = geometry };

    public static GeometryMutationResult Failure(string errorMessage)
        => new() { IsValid = false, ErrorMessage = errorMessage };
}

/// <summary>
/// Metadata v2 attribute-validation extensions for mutation adapters.
/// </summary>
internal static class MetadataV2AttributeValidation
{
    public static ValidationResult<ImmutableDictionary<string, object?>> ValidateAttributesV2(
        this MetadataV2Resource resource,
        IReadOnlyDictionary<string, object?>? attributes,
        ValidationExtensions.AttributeValidationMode mode = ValidationExtensions.AttributeValidationMode.Strict,
        bool isUpdate = false)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (attributes == null || attributes.Count == 0)
        {
            return ValidationResult<ImmutableDictionary<string, object?>>.Success(
                ImmutableDictionary.Create<string, object?>(StringComparer.OrdinalIgnoreCase));
        }

        var fieldsByName = new Dictionary<string, MetadataV2Field>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in resource.SchemaFields)
        {
            fieldsByName.TryAdd(field.Name, field);
        }

        var errors = new List<string>();
        var builder = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, rawValue) in attributes)
        {
            if (ValidationExtensions.IsReservedMutationAttribute(key))
            {
                builder[key] = NormalizeAttributeValue(rawValue);
                continue;
            }

            if (!fieldsByName.TryGetValue(key, out var field))
            {
                if (mode == ValidationExtensions.AttributeValidationMode.GeoServices)
                {
                    continue;
                }
                errors.Add($"Unknown field '{key}'.");
                continue;
            }

            // Non-editable fields are read-only on update. On insert we still
            // accept supplied values (mirrors the v1 behaviour where Editable
            // didn't gate writes).
            if (isUpdate && !field.Editable)
            {
                errors.Add($"Field '{field.Name}' is not editable.");
                continue;
            }

            var normalizedValue = NormalizeAttributeValue(rawValue);
            var error = ValidateAttributeValue(field, normalizedValue, mode);
            if (error != null)
            {
                errors.Add(error);
                continue;
            }

            builder[field.Name] = normalizedValue;
        }

        if (errors.Count > 0)
        {
            var message = errors.Count == 1
                ? errors[0]
                : $"Multiple attribute validation errors: {string.Join("; ", errors)}";
            return ValidationResult<ImmutableDictionary<string, object?>>.Failure(message);
        }

        return ValidationResult<ImmutableDictionary<string, object?>>.Success(builder.ToImmutable());
    }

    private static object? NormalizeAttributeValue(object? value)
    {
        if (value == null)
        {
            return null;
        }

        if (value is JsonElement jsonElement)
        {
            return JsonElementConverter.ConvertToObject(jsonElement);
        }

        if (value is IReadOnlyDictionary<string, object?> readOnlyDict)
        {
            return readOnlyDict.ToDictionary(kvp => kvp.Key, kvp => NormalizeAttributeValue(kvp.Value));
        }

        if (value is IDictionary<string, object?> dict)
        {
            return dict.ToDictionary(kvp => kvp.Key, kvp => NormalizeAttributeValue(kvp.Value));
        }

        if (value is System.Collections.IEnumerable enumerable && value is not string)
        {
            var list = new List<object?>();
            foreach (var item in enumerable)
            {
                list.Add(NormalizeAttributeValue(item));
            }
            return list.ToArray();
        }

        return value;
    }

    private static string? ValidateAttributeValue(
        MetadataV2Field field,
        object? value,
        ValidationExtensions.AttributeValidationMode mode)
    {
        if (value == null)
        {
            return field.Nullable ? null : $"Field '{field.Name}' cannot be null.";
        }

        var typeError = ValidateAgainstFieldType(field, value, mode);
        if (typeError != null)
        {
            return typeError;
        }

        // Domain check applies on top of type validation.
        if (field.Domain is { } domain)
        {
            var domainError = ValidateAgainstDomain(field, value, domain);
            if (domainError != null)
            {
                return domainError;
            }
        }

        return null;
    }

    private static string? ValidateAgainstFieldType(
        MetadataV2Field field,
        object value,
        ValidationExtensions.AttributeValidationMode mode)
    {
        switch (field.Type)
        {
            case MetadataV2FieldType.String:
                if (value is not string stringValue)
                {
                    return $"Field '{field.Name}' must be a string.";
                }
                if (field.Length.HasValue && stringValue.Length > field.Length.Value)
                {
                    return $"Field '{field.Name}' exceeds maximum length of {field.Length.Value}.";
                }
                return null;

            case MetadataV2FieldType.Integer:
                if (!TryGetInt64(value, out var int32Value))
                {
                    return $"Field '{field.Name}' must be an integer.";
                }
                if (int32Value < int.MinValue || int32Value > int.MaxValue)
                {
                    return $"Field '{field.Name}' must be within 32-bit integer range.";
                }
                return null;

            case MetadataV2FieldType.BigInteger:
                return TryGetInt64(value, out _)
                    ? null
                    : $"Field '{field.Name}' must be a 64-bit integer.";

            case MetadataV2FieldType.Double:
            case MetadataV2FieldType.Float:
                return TryGetDouble(value, out _)
                    ? null
                    : $"Field '{field.Name}' must be a number.";

            case MetadataV2FieldType.Boolean:
                if (value is bool)
                {
                    return null;
                }
                if (mode == ValidationExtensions.AttributeValidationMode.GeoServices && TryGetBooleanFromNumeric(value))
                {
                    return null;
                }
                return $"Field '{field.Name}' must be a boolean.";

            case MetadataV2FieldType.DateTime:
                return ValidateDateTimeValue(field.Name, value,
                    allowNumeric: mode == ValidationExtensions.AttributeValidationMode.GeoServices);

            case MetadataV2FieldType.Date:
                return ValidateDateValue(field.Name, value,
                    allowNumeric: mode == ValidationExtensions.AttributeValidationMode.GeoServices);

            case MetadataV2FieldType.Time:
                return ValidateTimeValue(field.Name, value);

            case MetadataV2FieldType.Json:
                return null;

            case MetadataV2FieldType.Binary:
                if (value is byte[])
                {
                    return null;
                }
                if (value is string base64Value)
                {
                    return TryValidateBase64(base64Value)
                        ? null
                        : $"Field '{field.Name}' must be a valid Base64 string.";
                }
                return $"Field '{field.Name}' must be binary data.";

            case MetadataV2FieldType.Uuid:
                if (value is Guid)
                {
                    return null;
                }
                if (value is string uuidString && Guid.TryParse(uuidString, out _))
                {
                    return null;
                }
                return $"Field '{field.Name}' must be a UUID.";

            case MetadataV2FieldType.Geometry:
            case MetadataV2FieldType.Geography:
                // Geometry fields flow through the dedicated geometry pipeline,
                // not this validator.
                return null;

            default:
                return null;
        }
    }

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
            foreach (var coded in domain.CodedValues)
            {
                if (CodedValueMatches(coded.Code, value))
                {
                    return null;
                }
            }
            return $"Field '{field.Name}' value is not in the coded-value domain.";
        }

        if (string.Equals(domain.Type, "range", StringComparison.OrdinalIgnoreCase))
        {
            if (domain.Range is null || domain.Range.Count != 2)
            {
                return null;
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

        return null;
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

    private static string? ValidateDateTimeValue(string fieldName, object value, bool allowNumeric)
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
        if (allowNumeric && IsNumeric(value))
        {
            return null;
        }
        return $"Field '{fieldName}' must be a valid date-time value.";
    }

    private static string? ValidateDateValue(string fieldName, object value, bool allowNumeric)
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
        if (allowNumeric && IsNumeric(value))
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

    private static bool TryGetBooleanFromNumeric(object value)
    {
        if (TryGetInt64(value, out var longValue))
        {
            return longValue is 0 or 1;
        }
        if (TryGetDouble(value, out var doubleValue))
        {
            return doubleValue is 0 or 1;
        }
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

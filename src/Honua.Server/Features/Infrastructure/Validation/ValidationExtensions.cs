// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;

namespace Honua.Server.Features.Infrastructure.Validation;

/// <summary>
/// Extension methods for common validation patterns used across endpoints.
/// Provides fluent API for validation composition and reduces boilerplate code.
/// </summary>
public static class ValidationExtensions
{
    /// <summary>
    /// Attribute validation behavior by protocol.
    /// </summary>
    public enum AttributeValidationMode
    {
        Strict,
        GeoServices
    }

    /// <summary>
    /// Validates service and layer existence in a single operation.
    /// Common pattern used across FeatureServer, OGC, and OData endpoints.
    /// </summary>
    /// <param name="catalogService">Layer catalog service</param>
    /// <param name="serviceId">Service identifier</param>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Validation result with service and layer if successful</returns>
    public static async Task<ValidationResult<(ServiceDefinition Service, LayerDefinition Layer)>> ValidateServiceAndLayerAsync(
        this ILayerCatalog catalogService,
        string serviceId,
        int layerId,
        CancellationToken cancellationToken = default)
    {
        // First check if service exists
        var service = await catalogService.GetServiceAsync(serviceId, cancellationToken);
        if (service == null)
        {
            return ValidationResult<(ServiceDefinition, LayerDefinition)>.Failure($"Service '{serviceId}' not found");
        }

        // Then check if layer exists in the service
        var layer = service.GetLayer(layerId);
        if (layer == null)
        {
            return ValidationResult<(ServiceDefinition, LayerDefinition)>.Failure(
                $"Layer {layerId} not found in service '{serviceId}'");
        }

        return ValidationResult<(ServiceDefinition, LayerDefinition)>.Success((service, layer));
    }

    /// <summary>
    /// Validates collection existence for OGC API Features endpoints.
    /// </summary>
    /// <param name="catalogService">Layer catalog service</param>
    /// <param name="collectionId">Collection identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Validation result with layer definition if successful</returns>
    public static async Task<ValidationResult<LayerDefinition>> ValidateCollectionAsync(
        this ILayerCatalog catalogService,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        // For OGC API Features, collection ID maps to layer ID
        if (!int.TryParse(collectionId, out var layerId))
        {
            return ValidationResult<LayerDefinition>.Failure($"Invalid collection ID '{collectionId}'");
        }

        var layer = await catalogService.GetLayerAsync(layerId, cancellationToken);
        if (layer == null)
        {
            return ValidationResult<LayerDefinition>.Failure($"Collection '{collectionId}' not found");
        }

        return ValidationResult<LayerDefinition>.Success(layer);
    }

    /// <summary>
    /// Validates that a field exists in the layer and is queryable.
    /// Used for dynamic query parameter validation in OGC API Features.
    /// </summary>
    /// <param name="layer">Layer definition</param>
    /// <param name="fieldName">Field name to validate</param>
    /// <returns>Validation result indicating if field is queryable</returns>
    public static ValidationResult ValidateQueryableField(this LayerDefinition layer, string fieldName)
    {
        var field = layer.AttributeFields.FirstOrDefault(f =>
            string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase));

        if (field == null)
        {
            return ValidationResult.Failure($"Field '{fieldName}' does not exist in layer");
        }

        // Check if field type is suitable for querying (simple types only)
        if (!IsSimpleQueryableType(field.Type))
        {
            return ValidationResult.Failure($"Field '{fieldName}' is not queryable (unsupported type: {field.Type})");
        }

        return ValidationResult.Success();
    }

    /// <summary>
    /// Validates output fields parameter against layer schema.
    /// </summary>
    /// <param name="layer">Layer definition</param>
    /// <param name="outFields">Comma-separated output fields string</param>
    /// <returns>Validation result with normalized field names if successful</returns>
    public static ValidationResult<string[]> ValidateOutputFields(this LayerDefinition layer, string? outFields)
    {
        // Handle special cases
        if (string.IsNullOrWhiteSpace(outFields) || outFields.Equals("*", StringComparison.OrdinalIgnoreCase))
        {
            return ValidationResult<string[]>.Success(null); // Return all fields
        }

        var requestedFields = outFields.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim())
            .Where(f => !string.IsNullOrEmpty(f))
            .ToArray();

        if (requestedFields.Length == 0)
        {
            return ValidationResult<string[]>.Success(null); // Return all fields
        }

        // Validate each requested field
        var invalidFields = new List<string>();
        var validFields = new List<string>();

        foreach (var requestedField in requestedFields)
        {
            var field = layer.AttributeFields.FirstOrDefault(f =>
                string.Equals(f.Name, requestedField, StringComparison.OrdinalIgnoreCase));

            if (field != null)
            {
                validFields.Add(field.Name); // Use the actual field name (correct casing)
            }
            else
            {
                invalidFields.Add(requestedField);
            }
        }

        if (invalidFields.Count > 0)
        {
            return ValidationResult<string[]>.Failure(
                $"Invalid field names: {string.Join(", ", invalidFields)}");
        }

        return ValidationResult<string[]>.Success(validFields.ToArray());
    }

    /// <summary>
    /// Validates attribute values against layer schema.
    /// </summary>
    public static ValidationResult<ImmutableDictionary<string, object?>> ValidateAttributes(
        this LayerDefinition layer,
        IReadOnlyDictionary<string, object?>? attributes,
        AttributeValidationMode mode = AttributeValidationMode.Strict)
    {
        if (attributes == null || attributes.Count == 0)
        {
            return ValidationResult<ImmutableDictionary<string, object?>>.Success(
                ImmutableDictionary.Create<string, object?>(StringComparer.OrdinalIgnoreCase));
        }

        var fieldsByName = layer.AttributeFields
            .ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

        var errors = new List<string>();
        var builder = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, rawValue) in attributes)
        {
            if (!fieldsByName.TryGetValue(key, out var field))
            {
                errors.Add($"Unknown field '{key}'.");
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

    /// <summary>
    /// Validates spatial relationship parameter.
    /// </summary>
    /// <param name="spatialRel">Spatial relationship string</param>
    /// <returns>Validation result with normalized spatial relationship</returns>
    public static ValidationResult<string> ValidateSpatialRelationship(string? spatialRel)
    {
        if (string.IsNullOrWhiteSpace(spatialRel))
        {
            return ValidationResult<string>.Success("esriSpatialRelIntersects"); // Default
        }

        var validRelationships = new[]
        {
            "esriSpatialRelIntersects",
            "esriSpatialRelContains",
            "esriSpatialRelCrosses",
            "esriSpatialRelEnvelopeIntersects",
            "esriSpatialRelIndexIntersects",
            "esriSpatialRelOverlaps",
            "esriSpatialRelTouches",
            "esriSpatialRelWithin"
        };

        var normalized = validRelationships.FirstOrDefault(vr =>
            string.Equals(vr, spatialRel, StringComparison.OrdinalIgnoreCase));

        if (normalized == null)
        {
            return ValidationResult<string>.Failure(
                $"Invalid spatial relationship '{spatialRel}'. Valid values: {string.Join(", ", validRelationships)}");
        }

        return ValidationResult<string>.Success(normalized);
    }

    /// <summary>
    /// Validates distance and units parameters for spatial queries.
    /// </summary>
    /// <param name="distance">Distance value</param>
    /// <param name="units">Distance units</param>
    /// <returns>Validation result for distance query parameters</returns>
    public static ValidationResult ValidateDistanceQuery(double? distance, string? units)
    {
        if (!distance.HasValue && string.IsNullOrWhiteSpace(units))
        {
            return ValidationResult.Success(); // No distance query
        }

        if (!distance.HasValue || distance.Value < 0)
        {
            return ValidationResult.Failure("Distance must be a non-negative number when specified");
        }

        if (string.IsNullOrWhiteSpace(units))
        {
            return ValidationResult.Failure("Units must be specified when distance is provided");
        }

        var validUnits = new[]
        {
            "esriSRUnit_Meter",
            "esriSRUnit_Kilometer",
            "esriSRUnit_Foot",
            "esriSRUnit_StatuteMile",
            "esriSRUnit_NauticalMile"
        };

        var normalizedUnits = validUnits.FirstOrDefault(vu =>
            string.Equals(vu, units, StringComparison.OrdinalIgnoreCase));

        if (normalizedUnits == null)
        {
            return ValidationResult.Failure(
                $"Invalid units '{units}'. Valid values: {string.Join(", ", validUnits)}");
        }

        return ValidationResult.Success();
    }

    private static object? NormalizeAttributeValue(object? value)
    {
        if (value == null)
        {
            return null;
        }

        if (value is JsonElement jsonElement)
        {
            return ConvertJsonElement(jsonElement);
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

    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var longVal) ? longVal :
                element.TryGetDouble(out var doubleVal) ? doubleVal :
                element.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(prop => prop.Name, prop => ConvertJsonElement(prop.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToArray(),
            _ => element.GetRawText()
        };
    }

    private static string? ValidateAttributeValue(
        FieldDefinition field,
        object? value,
        AttributeValidationMode mode)
    {
        if (value == null)
        {
            return field.Nullable ? null : $"Field '{field.Name}' cannot be null.";
        }

        switch (field.Type)
        {
            case FieldType.String:
            {
                if (value is not string stringValue)
                {
                    return $"Field '{field.Name}' must be a string.";
                }

                if (field.Length.HasValue && stringValue.Length > field.Length.Value)
                {
                    return $"Field '{field.Name}' exceeds maximum length of {field.Length.Value}.";
                }

                return null;
            }
            case FieldType.Integer:
            {
                if (!TryGetInt64(value, out var longValue))
                {
                    return $"Field '{field.Name}' must be an integer.";
                }

                if (longValue < int.MinValue || longValue > int.MaxValue)
                {
                    return $"Field '{field.Name}' must be within 32-bit integer range.";
                }

                return null;
            }
            case FieldType.BigInteger:
            {
                return TryGetInt64(value, out _)
                    ? null
                    : $"Field '{field.Name}' must be a 64-bit integer.";
            }
            case FieldType.Double:
            {
                return TryGetDouble(value, out _)
                    ? null
                    : $"Field '{field.Name}' must be a number.";
            }
            case FieldType.Float:
            {
                return TryGetDouble(value, out _)
                    ? null
                    : $"Field '{field.Name}' must be a number.";
            }
            case FieldType.Boolean:
            {
                if (value is bool)
                {
                    return null;
                }

                if (mode == AttributeValidationMode.GeoServices && TryGetBooleanFromNumeric(value, out _))
                {
                    return null;
                }

                return $"Field '{field.Name}' must be a boolean.";
            }
            case FieldType.DateTime:
            {
                return ValidateDateTimeValue(field.Name, value, allowNumeric: mode == AttributeValidationMode.GeoServices);
            }
            case FieldType.Date:
            {
                return ValidateDateValue(field.Name, value, allowNumeric: mode == AttributeValidationMode.GeoServices);
            }
            case FieldType.Time:
            {
                return ValidateTimeValue(field.Name, value);
            }
            case FieldType.Json:
            {
                return null;
            }
            case FieldType.Binary:
            {
                if (value is byte[])
                {
                    return null;
                }

                if (value is string stringValue)
                {
                    return TryValidateBase64(stringValue)
                        ? null
                        : $"Field '{field.Name}' must be a valid Base64 string.";
                }

                return $"Field '{field.Name}' must be binary data.";
            }
            case FieldType.Uuid:
            {
                if (value is Guid)
                {
                    return null;
                }

                if (value is string stringValue && Guid.TryParse(stringValue, out _))
                {
                    return null;
                }

                return $"Field '{field.Name}' must be a UUID.";
            }
            default:
                return null;
        }
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

    private static bool TryGetBooleanFromNumeric(object value, out bool result)
    {
        result = false;
        if (!TryGetInt64(value, out var longValue))
        {
            if (TryGetDouble(value, out var doubleValue))
            {
                if (doubleValue is 0 or 1)
                {
                    result = doubleValue == 1;
                    return true;
                }
            }

            return false;
        }

        if (longValue is 0 or 1)
        {
            result = longValue == 1;
            return true;
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

    /// <summary>
    /// Determines if a field type is suitable for simple querying.
    /// </summary>
    private static bool IsSimpleQueryableType(FieldType fieldType)
    {
        return fieldType switch
        {
            FieldType.String => true,
            FieldType.Integer => true,
            FieldType.BigInteger => true,
            FieldType.Double => true,
            FieldType.Float => true,
            FieldType.DateTime => true,
            FieldType.Date => true,
            FieldType.Time => true,
            FieldType.Boolean => true,
            FieldType.Uuid => true,
            _ => false // Exclude geometry, blob, raster, etc.
        };
    }

    /// <summary>
    /// Chains validation results, stopping at the first failure.
    /// </summary>
    /// <param name="validationResult">Initial validation result</param>
    /// <param name="nextValidation">Function to produce next validation if current succeeds</param>
    /// <returns>Combined validation result</returns>
    public static ValidationResult Then(this ValidationResult validationResult, Func<ValidationResult> nextValidation)
    {
        return validationResult.IsValid ? nextValidation() : validationResult;
    }

    /// <summary>
    /// Chains typed validation results, stopping at the first failure.
    /// </summary>
    /// <typeparam name="T">Type of first validation result</typeparam>
    /// <typeparam name="TNext">Type of second validation result</typeparam>
    /// <param name="validationResult">Initial validation result</param>
    /// <param name="nextValidation">Function to produce next validation using first result's value</param>
    /// <returns>Next validation result, or failure if first validation failed</returns>
    public static ValidationResult<TNext> Then<T, TNext>(
        this ValidationResult<T> validationResult,
        Func<T, ValidationResult<TNext>> nextValidation)
    {
        return validationResult.IsValid
            ? nextValidation(validationResult.Value!)
            : ValidationResult<TNext>.Failure(validationResult.ErrorMessage!);
    }
}

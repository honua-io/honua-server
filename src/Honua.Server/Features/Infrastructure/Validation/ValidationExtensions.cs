// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Validation;

namespace Honua.Infrastructure.Validation;

/// <summary>
/// Extension methods for common validation patterns used across endpoints.
/// Provides fluent API for validation composition and reduces boilerplate code.
/// </summary>
public static class ValidationExtensions
{
    internal const string WfsGmlIdentifierAttributeName = "__honua_wfs_gml_identifier";
    internal const string WfsGmlIdentifierCodeSpaceAttributeName = "__honua_wfs_gml_identifier_codespace";
    internal const string WfsGmlNameAttributeName = "__honua_wfs_gml_name";
    internal const string WfsGmlDescriptionAttributeName = "__honua_wfs_gml_description";

    /// <summary>
    /// Attribute validation behavior by protocol.
    /// </summary>
    public enum AttributeValidationMode
    {
        Strict,
        GeoServices
    }

    internal static bool IsReservedMutationAttribute(string attributeName)
    {
        return attributeName.Equals(WfsGmlIdentifierAttributeName, StringComparison.Ordinal) ||
               attributeName.Equals(WfsGmlIdentifierCodeSpaceAttributeName, StringComparison.Ordinal) ||
               attributeName.Equals(WfsGmlNameAttributeName, StringComparison.Ordinal) ||
               attributeName.Equals(WfsGmlDescriptionAttributeName, StringComparison.Ordinal);
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

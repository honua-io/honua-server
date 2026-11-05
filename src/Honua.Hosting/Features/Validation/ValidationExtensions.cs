// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Validation;

namespace Honua.Infrastructure.Validation;

public static class ValidationExtensions
{
    internal const string WfsGmlIdentifierAttributeName = "__honua_wfs_gml_identifier";
    internal const string WfsGmlIdentifierCodeSpaceAttributeName = "__honua_wfs_gml_identifier_codespace";
    internal const string WfsGmlNameAttributeName = "__honua_wfs_gml_name";
    internal const string WfsGmlDescriptionAttributeName = "__honua_wfs_gml_description";

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

    public static ValidationResult Then(this ValidationResult validationResult, Func<ValidationResult> nextValidation)
    {
        return validationResult.IsValid ? nextValidation() : validationResult;
    }

    public static ValidationResult<TNext> Then<T, TNext>(
        this ValidationResult<T> validationResult,
        Func<T, ValidationResult<TNext>> nextValidation)
    {
        return validationResult.IsValid
            ? nextValidation(validationResult.Value!)
            : ValidationResult<TNext>.Failure(validationResult.ErrorMessage!);
    }
}

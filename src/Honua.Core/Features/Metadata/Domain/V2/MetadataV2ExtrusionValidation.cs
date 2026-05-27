// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Stable, machine-readable codes used to report invalid Metadata v2 extrusion input.
/// </summary>
public static class MetadataV2ExtrusionErrorCodes
{
    /// <summary>The required height field name was missing or empty.</summary>
    public const string HeightFieldMissing = "EXTRUSION_HEIGHT_FIELD_MISSING";

    /// <summary>The configured height field does not exist on the resource.</summary>
    public const string HeightFieldNotFound = "EXTRUSION_HEIGHT_FIELD_NOT_FOUND";

    /// <summary>The configured height field is not a numeric type supported for extrusion.</summary>
    public const string HeightFieldTypeInvalid = "EXTRUSION_HEIGHT_FIELD_TYPE_INVALID";

    /// <summary>The configured base-height field does not exist on the resource.</summary>
    public const string BaseFieldNotFound = "EXTRUSION_BASE_FIELD_NOT_FOUND";

    /// <summary>The configured base-height field is not a numeric type supported for extrusion.</summary>
    public const string BaseFieldTypeInvalid = "EXTRUSION_BASE_FIELD_TYPE_INVALID";

    /// <summary>The configured vertical unit is not a recognized value.</summary>
    public const string UnitUnrecognized = "EXTRUSION_UNIT_UNRECOGNIZED";

    /// <summary>The configured default height is negative.</summary>
    public const string NegativeDefaultHeight = "EXTRUSION_NEGATIVE_DEFAULT_HEIGHT";
}

/// <summary>
/// Validates Metadata v2 extrusion configuration against resource schema fields.
/// </summary>
public static class MetadataV2ExtrusionValidator
{
    /// <summary>
    /// Validates the supplied extrusion configuration against the resource's field schema.
    /// </summary>
    /// <param name="extrusion">Extrusion configuration to validate.</param>
    /// <param name="fields">Fields available on the resource.</param>
    /// <returns>Stable error codes for each violation; empty when valid.</returns>
    public static IReadOnlyList<string> Validate(
        MetadataV2ExtrusionInfo extrusion,
        IReadOnlyList<MetadataV2Field> fields)
    {
        ArgumentNullException.ThrowIfNull(extrusion);
        ArgumentNullException.ThrowIfNull(fields);

        var errors = new List<string>(capacity: 4);

        if (string.IsNullOrWhiteSpace(extrusion.HeightField))
        {
            errors.Add(MetadataV2ExtrusionErrorCodes.HeightFieldMissing);
        }
        else
        {
            var heightField = FindField(fields, extrusion.HeightField);
            if (heightField is null)
            {
                errors.Add(MetadataV2ExtrusionErrorCodes.HeightFieldNotFound);
            }
            else if (!IsSupportedNumericType(heightField.Type))
            {
                errors.Add(MetadataV2ExtrusionErrorCodes.HeightFieldTypeInvalid);
            }
        }

        if (!string.IsNullOrWhiteSpace(extrusion.BaseHeightField))
        {
            var baseField = FindField(fields, extrusion.BaseHeightField);
            if (baseField is null)
            {
                errors.Add(MetadataV2ExtrusionErrorCodes.BaseFieldNotFound);
            }
            else if (!IsSupportedNumericType(baseField.Type))
            {
                errors.Add(MetadataV2ExtrusionErrorCodes.BaseFieldTypeInvalid);
            }
        }

        if (!string.IsNullOrWhiteSpace(extrusion.Unit)
            && !MetadataV2VerticalUnits.TryNormalize(extrusion.Unit, out _))
        {
            errors.Add(MetadataV2ExtrusionErrorCodes.UnitUnrecognized);
        }

        if (extrusion.DefaultHeight is { } defaultHeight && defaultHeight < 0)
        {
            errors.Add(MetadataV2ExtrusionErrorCodes.NegativeDefaultHeight);
        }

        return errors;
    }

    private static MetadataV2Field? FindField(IReadOnlyList<MetadataV2Field> fields, string name)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            if (string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return field;
            }
        }

        return null;
    }

    private static bool IsSupportedNumericType(MetadataV2FieldType type)
        => type is MetadataV2FieldType.Integer
            or MetadataV2FieldType.BigInteger
            or MetadataV2FieldType.Double
            or MetadataV2FieldType.Float;
}

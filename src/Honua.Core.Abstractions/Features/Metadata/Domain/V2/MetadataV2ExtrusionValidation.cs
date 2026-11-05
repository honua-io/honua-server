// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

public static class MetadataV2ExtrusionErrorCodes
{
    public const string HeightFieldMissing = "EXTRUSION_HEIGHT_FIELD_MISSING";

    public const string HeightFieldNotFound = "EXTRUSION_HEIGHT_FIELD_NOT_FOUND";

    public const string HeightFieldTypeInvalid = "EXTRUSION_HEIGHT_FIELD_TYPE_INVALID";

    public const string BaseFieldNotFound = "EXTRUSION_BASE_FIELD_NOT_FOUND";

    public const string BaseFieldTypeInvalid = "EXTRUSION_BASE_FIELD_TYPE_INVALID";

    public const string UnitUnrecognized = "EXTRUSION_UNIT_UNRECOGNIZED";

    public const string NegativeDefaultHeight = "EXTRUSION_NEGATIVE_DEFAULT_HEIGHT";
}

public static class MetadataV2ExtrusionValidator
{
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

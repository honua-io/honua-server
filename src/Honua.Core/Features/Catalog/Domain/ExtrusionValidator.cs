// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Catalog.Domain;

/// <summary>
/// Validates <see cref="LayerExtrusionInfo"/> instances against a layer's
/// field definitions. Errors are returned as stable, machine-readable
/// <see cref="ExtrusionErrorCodes"/> values so the metadata pipeline can
/// surface them through shared problem-detail helpers.
/// </summary>
public static class ExtrusionValidator
{
    /// <summary>
    /// Numeric field types supported as extrusion height/base sources.
    /// </summary>
    private static readonly FieldType[] _supportedNumericTypes =
    [
        FieldType.Integer,
        FieldType.BigInteger,
        FieldType.Double,
        FieldType.Float
    ];

    /// <summary>
    /// Validates the supplied extrusion configuration against the layer's
    /// field schema. Returns an empty list when the configuration is valid.
    /// </summary>
    /// <param name="extrusion">Extrusion configuration to validate.</param>
    /// <param name="fields">Fields available on the layer.</param>
    /// <returns>Stable error codes for each violation; empty when valid.</returns>
    public static IReadOnlyList<string> Validate(LayerExtrusionInfo extrusion, IReadOnlyList<FieldDefinition> fields)
    {
        ArgumentNullException.ThrowIfNull(extrusion);
        ArgumentNullException.ThrowIfNull(fields);

        var errors = new List<string>(capacity: 4);

        if (string.IsNullOrWhiteSpace(extrusion.HeightField))
        {
            errors.Add(ExtrusionErrorCodes.HeightFieldMissing);
        }
        else
        {
            var heightField = FindField(fields, extrusion.HeightField);
            if (heightField is null)
            {
                errors.Add(ExtrusionErrorCodes.HeightFieldNotFound);
            }
            else if (!IsSupportedNumericType(heightField.Type))
            {
                errors.Add(ExtrusionErrorCodes.HeightFieldTypeInvalid);
            }
        }

        if (!string.IsNullOrWhiteSpace(extrusion.BaseHeightField))
        {
            var baseField = FindField(fields, extrusion.BaseHeightField);
            if (baseField is null)
            {
                errors.Add(ExtrusionErrorCodes.BaseFieldNotFound);
            }
            else if (!IsSupportedNumericType(baseField.Type))
            {
                errors.Add(ExtrusionErrorCodes.BaseFieldTypeInvalid);
            }
        }

        if (!string.IsNullOrWhiteSpace(extrusion.Unit) &&
            !VerticalUnits.TryNormalize(extrusion.Unit, out _))
        {
            errors.Add(ExtrusionErrorCodes.UnitUnrecognized);
        }

        if (extrusion.DefaultHeight is { } defaultHeight && defaultHeight < 0)
        {
            errors.Add(ExtrusionErrorCodes.NegativeDefaultHeight);
        }

        return errors;
    }

    private static FieldDefinition? FindField(IReadOnlyList<FieldDefinition> fields, string name)
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

    private static bool IsSupportedNumericType(FieldType type)
    {
        for (var i = 0; i < _supportedNumericTypes.Length; i++)
        {
            if (_supportedNumericTypes[i] == type)
            {
                return true;
            }
        }

        return false;
    }
}

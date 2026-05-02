// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Catalog;

/// <summary>
/// Unit tests for <see cref="ExtrusionValidator"/>. Confirms that every
/// validation branch reports the expected stable error code from
/// <see cref="ExtrusionErrorCodes"/>.
/// </summary>
[Protocol(Protocols.GeoservicesCatalog)]
public sealed class ExtrusionValidatorTests
{
    private static readonly FieldDefinition[] _layerFields =
    [
        new("objectid", FieldType.Integer, Nullable: false),
        new("name", FieldType.String, Length: 64),
        new("height_m", FieldType.Double),
        new("base_m", FieldType.Float),
        new("levels", FieldType.Integer),
        new("levels_64", FieldType.BigInteger),
        new("active", FieldType.Boolean),
        new("shape", FieldType.Geometry, Nullable: false)
    ];

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_ValidConfig_ReturnsNoErrors()
    {
        var extrusion = new LayerExtrusionInfo
        {
            HeightField = "height_m",
            BaseHeightField = "base_m",
            Unit = VerticalUnit.Meters,
            DefaultHeight = 3.0
        };

        var errors = ExtrusionValidator.Validate(extrusion, _layerFields);

        errors.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_HeightFieldOnly_NoBaseField_IsValid()
    {
        var extrusion = new LayerExtrusionInfo
        {
            HeightField = "levels",
            Unit = VerticalUnit.Meters
        };

        var errors = ExtrusionValidator.Validate(extrusion, _layerFields);

        errors.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_HeightFieldEmpty_ReportsHeightFieldMissing()
    {
        var extrusion = new LayerExtrusionInfo
        {
            HeightField = string.Empty
        };

        var errors = ExtrusionValidator.Validate(extrusion, _layerFields);

        errors.Should().Contain(ExtrusionErrorCodes.HeightFieldMissing);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_HeightFieldWhitespace_ReportsHeightFieldMissing()
    {
        var extrusion = new LayerExtrusionInfo
        {
            HeightField = "   "
        };

        var errors = ExtrusionValidator.Validate(extrusion, _layerFields);

        errors.Should().Contain(ExtrusionErrorCodes.HeightFieldMissing);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_HeightFieldNotPresent_ReportsHeightFieldNotFound()
    {
        var extrusion = new LayerExtrusionInfo
        {
            HeightField = "nonexistent"
        };

        var errors = ExtrusionValidator.Validate(extrusion, _layerFields);

        errors.Should().Contain(ExtrusionErrorCodes.HeightFieldNotFound);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_HeightFieldNonNumeric_ReportsHeightFieldTypeInvalid()
    {
        var extrusion = new LayerExtrusionInfo
        {
            HeightField = "name"
        };

        var errors = ExtrusionValidator.Validate(extrusion, _layerFields);

        errors.Should().Contain(ExtrusionErrorCodes.HeightFieldTypeInvalid);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_HeightFieldGeometry_ReportsHeightFieldTypeInvalid()
    {
        var extrusion = new LayerExtrusionInfo
        {
            HeightField = "shape"
        };

        var errors = ExtrusionValidator.Validate(extrusion, _layerFields);

        errors.Should().Contain(ExtrusionErrorCodes.HeightFieldTypeInvalid);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_HeightFieldName_IsCaseInsensitive()
    {
        var extrusion = new LayerExtrusionInfo
        {
            HeightField = "HEIGHT_M"
        };

        var errors = ExtrusionValidator.Validate(extrusion, _layerFields);

        errors.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_BaseHeightFieldNotPresent_ReportsBaseFieldNotFound()
    {
        var extrusion = new LayerExtrusionInfo
        {
            HeightField = "height_m",
            BaseHeightField = "nonexistent"
        };

        var errors = ExtrusionValidator.Validate(extrusion, _layerFields);

        errors.Should().Contain(ExtrusionErrorCodes.BaseFieldNotFound);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_BaseHeightFieldNonNumeric_ReportsBaseFieldTypeInvalid()
    {
        var extrusion = new LayerExtrusionInfo
        {
            HeightField = "height_m",
            BaseHeightField = "active"
        };

        var errors = ExtrusionValidator.Validate(extrusion, _layerFields);

        errors.Should().Contain(ExtrusionErrorCodes.BaseFieldTypeInvalid);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_NumericFieldTypes_AreAllAccepted()
    {
        foreach (var fieldName in new[] { "height_m", "base_m", "levels", "levels_64" })
        {
            var extrusion = new LayerExtrusionInfo { HeightField = fieldName };
            var errors = ExtrusionValidator.Validate(extrusion, _layerFields);
            errors.Should().BeEmpty($"field '{fieldName}' is numeric and should be accepted");
        }
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_NegativeDefaultHeight_ReportsNegativeDefaultHeight()
    {
        var extrusion = new LayerExtrusionInfo
        {
            HeightField = "height_m",
            DefaultHeight = -1.5
        };

        var errors = ExtrusionValidator.Validate(extrusion, _layerFields);

        errors.Should().Contain(ExtrusionErrorCodes.NegativeDefaultHeight);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_ZeroDefaultHeight_IsValid()
    {
        var extrusion = new LayerExtrusionInfo
        {
            HeightField = "height_m",
            DefaultHeight = 0.0
        };

        var errors = ExtrusionValidator.Validate(extrusion, _layerFields);

        errors.Should().NotContain(ExtrusionErrorCodes.NegativeDefaultHeight);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_UnitOutOfRange_ReportsUnitUnrecognized()
    {
        var extrusion = new LayerExtrusionInfo
        {
            HeightField = "height_m",
            Unit = (VerticalUnit)int.MaxValue
        };

        var errors = ExtrusionValidator.Validate(extrusion, _layerFields);

        errors.Should().Contain(ExtrusionErrorCodes.UnitUnrecognized);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_AllRecognizedUnits_AreAccepted()
    {
        foreach (var unit in Enum.GetValues<VerticalUnit>())
        {
            var extrusion = new LayerExtrusionInfo
            {
                HeightField = "height_m",
                Unit = unit
            };

            var errors = ExtrusionValidator.Validate(extrusion, _layerFields);
            errors.Should().NotContain(ExtrusionErrorCodes.UnitUnrecognized,
                $"unit {unit} should be recognized");
        }
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_MultipleViolations_ReportsAllErrors()
    {
        var extrusion = new LayerExtrusionInfo
        {
            HeightField = "name",
            BaseHeightField = "active",
            DefaultHeight = -2.0,
            Unit = (VerticalUnit)999
        };

        var errors = ExtrusionValidator.Validate(extrusion, _layerFields);

        errors.Should().Contain(ExtrusionErrorCodes.HeightFieldTypeInvalid);
        errors.Should().Contain(ExtrusionErrorCodes.BaseFieldTypeInvalid);
        errors.Should().Contain(ExtrusionErrorCodes.NegativeDefaultHeight);
        errors.Should().Contain(ExtrusionErrorCodes.UnitUnrecognized);
    }
}

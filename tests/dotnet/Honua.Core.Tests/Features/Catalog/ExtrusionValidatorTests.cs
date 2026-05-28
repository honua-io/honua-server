// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

/// <summary>
/// Unit tests for <see cref="MetadataV2ExtrusionValidator"/>. Confirms that every
/// validation branch reports the expected stable error code from
/// <see cref="MetadataV2ExtrusionErrorCodes"/>.
/// </summary>
[Protocol(Protocols.GeoservicesCatalog)]
public sealed class MetadataV2ExtrusionValidatorTests
{
    private static readonly MetadataV2Field[] _resourceFields =
    [
        new() { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false },
        new() { Name = "name", Type = MetadataV2FieldType.String, Length = 64 },
        new() { Name = "height_m", Type = MetadataV2FieldType.Double },
        new() { Name = "base_m", Type = MetadataV2FieldType.Float },
        new() { Name = "levels", Type = MetadataV2FieldType.Integer },
        new() { Name = "levels_64", Type = MetadataV2FieldType.BigInteger },
        new() { Name = "active", Type = MetadataV2FieldType.Boolean },
        new() { Name = "shape", Type = MetadataV2FieldType.Geometry, Nullable = false }
    ];

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_ValidConfig_ReturnsNoErrors()
    {
        var extrusion = new MetadataV2ExtrusionInfo
        {
            HeightField = "height_m",
            BaseHeightField = "base_m",
            Unit = MetadataV2VerticalUnits.Meters,
            DefaultHeight = 3.0
        };

        var errors = MetadataV2ExtrusionValidator.Validate(extrusion, _resourceFields);

        errors.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_HeightFieldOnly_NoBaseField_IsValid()
    {
        var extrusion = new MetadataV2ExtrusionInfo
        {
            HeightField = "levels",
            Unit = MetadataV2VerticalUnits.Meters
        };

        var errors = MetadataV2ExtrusionValidator.Validate(extrusion, _resourceFields);

        errors.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_HeightFieldEmpty_ReportsHeightFieldMissing()
    {
        var extrusion = new MetadataV2ExtrusionInfo
        {
            HeightField = string.Empty
        };

        var errors = MetadataV2ExtrusionValidator.Validate(extrusion, _resourceFields);

        errors.Should().Contain(MetadataV2ExtrusionErrorCodes.HeightFieldMissing);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_HeightFieldWhitespace_ReportsHeightFieldMissing()
    {
        var extrusion = new MetadataV2ExtrusionInfo
        {
            HeightField = "   "
        };

        var errors = MetadataV2ExtrusionValidator.Validate(extrusion, _resourceFields);

        errors.Should().Contain(MetadataV2ExtrusionErrorCodes.HeightFieldMissing);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_HeightFieldNotPresent_ReportsHeightFieldNotFound()
    {
        var extrusion = new MetadataV2ExtrusionInfo
        {
            HeightField = "nonexistent"
        };

        var errors = MetadataV2ExtrusionValidator.Validate(extrusion, _resourceFields);

        errors.Should().Contain(MetadataV2ExtrusionErrorCodes.HeightFieldNotFound);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_HeightFieldNonNumeric_ReportsHeightFieldTypeInvalid()
    {
        var extrusion = new MetadataV2ExtrusionInfo
        {
            HeightField = "name"
        };

        var errors = MetadataV2ExtrusionValidator.Validate(extrusion, _resourceFields);

        errors.Should().Contain(MetadataV2ExtrusionErrorCodes.HeightFieldTypeInvalid);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_HeightFieldGeometry_ReportsHeightFieldTypeInvalid()
    {
        var extrusion = new MetadataV2ExtrusionInfo
        {
            HeightField = "shape"
        };

        var errors = MetadataV2ExtrusionValidator.Validate(extrusion, _resourceFields);

        errors.Should().Contain(MetadataV2ExtrusionErrorCodes.HeightFieldTypeInvalid);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_HeightFieldName_IsCaseInsensitive()
    {
        var extrusion = new MetadataV2ExtrusionInfo
        {
            HeightField = "HEIGHT_M"
        };

        var errors = MetadataV2ExtrusionValidator.Validate(extrusion, _resourceFields);

        errors.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_BaseHeightFieldNotPresent_ReportsBaseFieldNotFound()
    {
        var extrusion = new MetadataV2ExtrusionInfo
        {
            HeightField = "height_m",
            BaseHeightField = "nonexistent"
        };

        var errors = MetadataV2ExtrusionValidator.Validate(extrusion, _resourceFields);

        errors.Should().Contain(MetadataV2ExtrusionErrorCodes.BaseFieldNotFound);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_BaseHeightFieldNonNumeric_ReportsBaseFieldTypeInvalid()
    {
        var extrusion = new MetadataV2ExtrusionInfo
        {
            HeightField = "height_m",
            BaseHeightField = "active"
        };

        var errors = MetadataV2ExtrusionValidator.Validate(extrusion, _resourceFields);

        errors.Should().Contain(MetadataV2ExtrusionErrorCodes.BaseFieldTypeInvalid);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_NumericFieldTypes_AreAllAccepted()
    {
        foreach (var fieldName in new[] { "height_m", "base_m", "levels", "levels_64" })
        {
            var extrusion = new MetadataV2ExtrusionInfo { HeightField = fieldName };
            var errors = MetadataV2ExtrusionValidator.Validate(extrusion, _resourceFields);
            errors.Should().BeEmpty($"field '{fieldName}' is numeric and should be accepted");
        }
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_NegativeDefaultHeight_ReportsNegativeDefaultHeight()
    {
        var extrusion = new MetadataV2ExtrusionInfo
        {
            HeightField = "height_m",
            DefaultHeight = -1.5
        };

        var errors = MetadataV2ExtrusionValidator.Validate(extrusion, _resourceFields);

        errors.Should().Contain(MetadataV2ExtrusionErrorCodes.NegativeDefaultHeight);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_ZeroDefaultHeight_IsValid()
    {
        var extrusion = new MetadataV2ExtrusionInfo
        {
            HeightField = "height_m",
            DefaultHeight = 0.0
        };

        var errors = MetadataV2ExtrusionValidator.Validate(extrusion, _resourceFields);

        errors.Should().NotContain(MetadataV2ExtrusionErrorCodes.NegativeDefaultHeight);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_UnknownUnit_ReportsUnitUnrecognized()
    {
        var extrusion = new MetadataV2ExtrusionInfo
        {
            HeightField = "height_m",
            Unit = "yards"
        };

        var errors = MetadataV2ExtrusionValidator.Validate(extrusion, _resourceFields);

        errors.Should().Contain(MetadataV2ExtrusionErrorCodes.UnitUnrecognized);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_AllRecognizedUnits_AreAccepted()
    {
        foreach (var unit in new[]
                 {
                     MetadataV2VerticalUnits.Meters,
                     MetadataV2VerticalUnits.Feet,
                     MetadataV2VerticalUnits.UsSurveyFeet
                 })
        {
            var extrusion = new MetadataV2ExtrusionInfo
            {
                HeightField = "height_m",
                Unit = unit
            };

            var errors = MetadataV2ExtrusionValidator.Validate(extrusion, _resourceFields);
            errors.Should().NotContain(MetadataV2ExtrusionErrorCodes.UnitUnrecognized,
                $"unit {unit} should be recognized");
        }
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_RecognizedUnit_IsCaseInsensitive()
    {
        foreach (var unit in new[] { "METERS", "Feet", "USSURVEYFEET" })
        {
            var extrusion = new MetadataV2ExtrusionInfo
            {
                HeightField = "height_m",
                Unit = unit
            };

            var errors = MetadataV2ExtrusionValidator.Validate(extrusion, _resourceFields);
            errors.Should().NotContain(MetadataV2ExtrusionErrorCodes.UnitUnrecognized,
                $"unit '{unit}' should be recognized case-insensitively");
        }
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_NullUnit_DefaultsToMetersWithNoError()
    {
        var extrusion = new MetadataV2ExtrusionInfo
        {
            HeightField = "height_m",
            Unit = null
        };

        var errors = MetadataV2ExtrusionValidator.Validate(extrusion, _resourceFields);

        errors.Should().NotContain(MetadataV2ExtrusionErrorCodes.UnitUnrecognized);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_MultipleViolations_ReportsAllErrors()
    {
        var extrusion = new MetadataV2ExtrusionInfo
        {
            HeightField = "name",
            BaseHeightField = "active",
            DefaultHeight = -2.0,
            Unit = "yards"
        };

        var errors = MetadataV2ExtrusionValidator.Validate(extrusion, _resourceFields);

        errors.Should().Contain(MetadataV2ExtrusionErrorCodes.HeightFieldTypeInvalid);
        errors.Should().Contain(MetadataV2ExtrusionErrorCodes.BaseFieldTypeInvalid);
        errors.Should().Contain(MetadataV2ExtrusionErrorCodes.NegativeDefaultHeight);
        errors.Should().Contain(MetadataV2ExtrusionErrorCodes.UnitUnrecognized);
    }
}

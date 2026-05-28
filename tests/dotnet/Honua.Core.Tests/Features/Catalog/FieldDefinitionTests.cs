// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Catalog;

/// <summary>
/// Unit coverage for <see cref="FieldDefinition"/>. The record powers schema
/// generation (SQL), GeoServices field metadata, and validation feedback —
/// the mappings ship as part of the wire contract (#1144).
/// </summary>
public sealed class FieldDefinitionTests
{
    [UnitTest]
    public void DisplayName_FallsBackToName_WhenDescriptionMissing()
    {
        var field = new FieldDefinition("objectid", MetadataV2FieldType.Integer);

        field.DisplayName.Should().Be("objectid");
    }

    [UnitTest]
    public void DisplayName_UsesDescriptionWhenProvided()
    {
        var field = new FieldDefinition("oid", MetadataV2FieldType.Integer, Description: "Object Identifier");

        field.DisplayName.Should().Be("Object Identifier");
    }

    [UnitTest]
    public void IsGeometry_ReflectsType()
    {
        new FieldDefinition("shape", MetadataV2FieldType.Geometry).IsGeometry.Should().BeTrue();
        new FieldDefinition("name", MetadataV2FieldType.String).IsGeometry.Should().BeFalse();
    }

    [UnitTest]
    public void IsVisible_InvertsIsHidden()
    {
        new FieldDefinition("a", MetadataV2FieldType.String).IsVisible.Should().BeTrue();
        new FieldDefinition("a", MetadataV2FieldType.String, IsHidden: true).IsVisible.Should().BeFalse();
    }

    [Theory]
    [InlineData(MetadataV2FieldType.String, "esriFieldTypeString")]
    [InlineData(MetadataV2FieldType.Integer, "esriFieldTypeInteger")]
    [InlineData(MetadataV2FieldType.BigInteger, "esriFieldTypeInteger64")]
    [InlineData(MetadataV2FieldType.Double, "esriFieldTypeDouble")]
    [InlineData(MetadataV2FieldType.Float, "esriFieldTypeSingle")]
    [InlineData(MetadataV2FieldType.Boolean, "esriFieldTypeSmallInteger")]
    [InlineData(MetadataV2FieldType.DateTime, "esriFieldTypeDate")]
    [InlineData(MetadataV2FieldType.Date, "esriFieldTypeDate")]
    [InlineData(MetadataV2FieldType.Time, "esriFieldTypeString")]
    [InlineData(MetadataV2FieldType.Geometry, "esriFieldTypeGeometry")]
    [InlineData(MetadataV2FieldType.Json, "esriFieldTypeString")]
    [InlineData(MetadataV2FieldType.Binary, "esriFieldTypeBlob")]
    [InlineData(MetadataV2FieldType.Uuid, "esriFieldTypeGUID")]
    public void GeoServicesType_MapsEveryFieldType(MetadataV2FieldType type, string expected)
    {
        var field = new FieldDefinition("f", type);

        field.GeoServicesType.Should().Be(expected);
    }

    [Theory]
    [InlineData(MetadataV2FieldType.Integer, "INTEGER")]
    [InlineData(MetadataV2FieldType.BigInteger, "BIGINT")]
    [InlineData(MetadataV2FieldType.Double, "DOUBLE PRECISION")]
    [InlineData(MetadataV2FieldType.Float, "REAL")]
    [InlineData(MetadataV2FieldType.Boolean, "BOOLEAN")]
    [InlineData(MetadataV2FieldType.DateTime, "TIMESTAMP WITH TIME ZONE")]
    [InlineData(MetadataV2FieldType.Date, "DATE")]
    [InlineData(MetadataV2FieldType.Time, "TIME")]
    [InlineData(MetadataV2FieldType.Geometry, "GEOMETRY")]
    [InlineData(MetadataV2FieldType.Json, "JSONB")]
    [InlineData(MetadataV2FieldType.Binary, "BYTEA")]
    [InlineData(MetadataV2FieldType.Uuid, "UUID")]
    public void SqlType_PrimitiveMappingIsStable(MetadataV2FieldType type, string expected)
    {
        new FieldDefinition("f", type).SqlType.Should().Be(expected);
    }

    [UnitTest]
    public void SqlType_StringWithLength_RendersVarchar()
    {
        new FieldDefinition("name", MetadataV2FieldType.String, Length: 80).SqlType.Should().Be("VARCHAR(80)");
    }

    [UnitTest]
    public void SqlType_StringWithoutLength_RendersText()
    {
        new FieldDefinition("name", MetadataV2FieldType.String).SqlType.Should().Be("TEXT");
    }

    [UnitTest]
    public void Validate_NameRequired()
    {
        new FieldDefinition("", MetadataV2FieldType.String).Validate()
            .Should().Contain("cannot be empty");
        new FieldDefinition("   ", MetadataV2FieldType.String).Validate()
            .Should().Contain("cannot be empty");
    }

    [UnitTest]
    public void Validate_NameAtBoundary_IsValid()
    {
        var name = new string('x', 64);

        new FieldDefinition(name, MetadataV2FieldType.String).Validate().Should().BeNull();
    }

    [UnitTest]
    public void Validate_NameTooLong_ReportsError()
    {
        var name = new string('x', 65);

        new FieldDefinition(name, MetadataV2FieldType.String).Validate()
            .Should().Contain("64 characters");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-1000)]
    public void Validate_NonPositiveStringLength_ReportsError(int length)
    {
        new FieldDefinition("name", MetadataV2FieldType.String, Length: length).Validate()
            .Should().Contain("positive");
    }

    [UnitTest]
    public void Validate_StringLengthAtBoundary_IsValid()
    {
        new FieldDefinition("name", MetadataV2FieldType.String, Length: 8000).Validate().Should().BeNull();
    }

    [UnitTest]
    public void Validate_StringLengthAboveBoundary_ReportsError()
    {
        new FieldDefinition("name", MetadataV2FieldType.String, Length: 8001).Validate()
            .Should().Contain("8000 characters");
    }

    [UnitTest]
    public void Validate_NumericLengthIgnored()
    {
        // Length only applies to string fields — supplying it on a numeric
        // field is permitted (and silently ignored by SqlType).
        new FieldDefinition("levels", MetadataV2FieldType.Integer, Length: 99).Validate().Should().BeNull();
    }

    [UnitTest]
    public void Defaults_NullableIsTrue_AndIsHiddenIsFalse()
    {
        var field = new FieldDefinition("name", MetadataV2FieldType.String);

        field.Nullable.Should().BeTrue();
        field.IsHidden.Should().BeFalse();
        field.IsVisible.Should().BeTrue();
    }
}

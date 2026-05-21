// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
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
        var field = new FieldDefinition("objectid", FieldType.Integer);

        field.DisplayName.Should().Be("objectid");
    }

    [UnitTest]
    public void DisplayName_UsesDescriptionWhenProvided()
    {
        var field = new FieldDefinition("oid", FieldType.Integer, Description: "Object Identifier");

        field.DisplayName.Should().Be("Object Identifier");
    }

    [UnitTest]
    public void IsGeometry_ReflectsType()
    {
        new FieldDefinition("shape", FieldType.Geometry).IsGeometry.Should().BeTrue();
        new FieldDefinition("name", FieldType.String).IsGeometry.Should().BeFalse();
    }

    [UnitTest]
    public void IsVisible_InvertsIsHidden()
    {
        new FieldDefinition("a", FieldType.String).IsVisible.Should().BeTrue();
        new FieldDefinition("a", FieldType.String, IsHidden: true).IsVisible.Should().BeFalse();
    }

    [Theory]
    [InlineData(FieldType.String, "esriFieldTypeString")]
    [InlineData(FieldType.Integer, "esriFieldTypeInteger")]
    [InlineData(FieldType.BigInteger, "esriFieldTypeInteger64")]
    [InlineData(FieldType.Double, "esriFieldTypeDouble")]
    [InlineData(FieldType.Float, "esriFieldTypeSingle")]
    [InlineData(FieldType.Boolean, "esriFieldTypeSmallInteger")]
    [InlineData(FieldType.DateTime, "esriFieldTypeDate")]
    [InlineData(FieldType.Date, "esriFieldTypeDate")]
    [InlineData(FieldType.Time, "esriFieldTypeString")]
    [InlineData(FieldType.Geometry, "esriFieldTypeGeometry")]
    [InlineData(FieldType.Json, "esriFieldTypeString")]
    [InlineData(FieldType.Binary, "esriFieldTypeBlob")]
    [InlineData(FieldType.Uuid, "esriFieldTypeGUID")]
    public void GeoServicesType_MapsEveryFieldType(FieldType type, string expected)
    {
        var field = new FieldDefinition("f", type);

        field.GeoServicesType.Should().Be(expected);
    }

    [Theory]
    [InlineData(FieldType.Integer, "INTEGER")]
    [InlineData(FieldType.BigInteger, "BIGINT")]
    [InlineData(FieldType.Double, "DOUBLE PRECISION")]
    [InlineData(FieldType.Float, "REAL")]
    [InlineData(FieldType.Boolean, "BOOLEAN")]
    [InlineData(FieldType.DateTime, "TIMESTAMP WITH TIME ZONE")]
    [InlineData(FieldType.Date, "DATE")]
    [InlineData(FieldType.Time, "TIME")]
    [InlineData(FieldType.Geometry, "GEOMETRY")]
    [InlineData(FieldType.Json, "JSONB")]
    [InlineData(FieldType.Binary, "BYTEA")]
    [InlineData(FieldType.Uuid, "UUID")]
    public void SqlType_PrimitiveMappingIsStable(FieldType type, string expected)
    {
        new FieldDefinition("f", type).SqlType.Should().Be(expected);
    }

    [UnitTest]
    public void SqlType_StringWithLength_RendersVarchar()
    {
        new FieldDefinition("name", FieldType.String, Length: 80).SqlType.Should().Be("VARCHAR(80)");
    }

    [UnitTest]
    public void SqlType_StringWithoutLength_RendersText()
    {
        new FieldDefinition("name", FieldType.String).SqlType.Should().Be("TEXT");
    }

    [UnitTest]
    public void Validate_NameRequired()
    {
        new FieldDefinition("", FieldType.String).Validate()
            .Should().Contain("cannot be empty");
        new FieldDefinition("   ", FieldType.String).Validate()
            .Should().Contain("cannot be empty");
    }

    [UnitTest]
    public void Validate_NameAtBoundary_IsValid()
    {
        var name = new string('x', 64);

        new FieldDefinition(name, FieldType.String).Validate().Should().BeNull();
    }

    [UnitTest]
    public void Validate_NameTooLong_ReportsError()
    {
        var name = new string('x', 65);

        new FieldDefinition(name, FieldType.String).Validate()
            .Should().Contain("64 characters");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-1000)]
    public void Validate_NonPositiveStringLength_ReportsError(int length)
    {
        new FieldDefinition("name", FieldType.String, Length: length).Validate()
            .Should().Contain("positive");
    }

    [UnitTest]
    public void Validate_StringLengthAtBoundary_IsValid()
    {
        new FieldDefinition("name", FieldType.String, Length: 8000).Validate().Should().BeNull();
    }

    [UnitTest]
    public void Validate_StringLengthAboveBoundary_ReportsError()
    {
        new FieldDefinition("name", FieldType.String, Length: 8001).Validate()
            .Should().Contain("8000 characters");
    }

    [UnitTest]
    public void Validate_NumericLengthIgnored()
    {
        // Length only applies to string fields — supplying it on a numeric
        // field is permitted (and silently ignored by SqlType).
        new FieldDefinition("levels", FieldType.Integer, Length: 99).Validate().Should().BeNull();
    }

    [UnitTest]
    public void Defaults_NullableIsTrue_AndIsHiddenIsFalse()
    {
        var field = new FieldDefinition("name", FieldType.String);

        field.Nullable.Should().BeTrue();
        field.IsHidden.Should().BeFalse();
        field.IsVisible.Should().BeTrue();
    }
}

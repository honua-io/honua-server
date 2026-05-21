// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Catalog;

/// <summary>
/// Unit coverage for <see cref="LayerStorageMapping"/>. Exercises the
/// validate / qualified-name / source-backed pathways that feed capability
/// reporting and storage routing decisions (#1144).
/// </summary>
public sealed class LayerStorageMappingTests
{
    [UnitTest]
    public void Default_PrimaryKey_AndGeometryColumn_AreApplied()
    {
        var mapping = new LayerStorageMapping(TableName: "features");

        mapping.PrimaryKeyColumn.Should().NotBeNullOrWhiteSpace();
        mapping.GeometryColumn.Should().Be("geometry");
        mapping.SchemaName.Should().BeNull();
        mapping.ProviderOptions.Should().NotBeNull().And.BeEmpty();
        mapping.IsSourceBacked.Should().BeFalse();
    }

    [UnitTest]
    public void Validate_ValidMapping_ReturnsEmpty()
    {
        var mapping = new LayerStorageMapping(
            TableName: "features",
            SchemaName: "public",
            PrimaryKeyColumn: "objectid",
            StorageSrid: 4326);

        mapping.Validate().Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validate_BlankTableName_ReportsError(string? tableName)
    {
        var mapping = new LayerStorageMapping(TableName: tableName!);

        mapping.Validate().Should().Contain(e => e.Contains("table", System.StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validate_BlankPrimaryKey_ReportsError(string? pk)
    {
        var mapping = new LayerStorageMapping(TableName: "features", PrimaryKeyColumn: pk!);

        mapping.Validate().Should().Contain(e => e.Contains("primary key", System.StringComparison.OrdinalIgnoreCase));
    }

    [UnitTest]
    public void Validate_ZeroOrNegativeSrid_ReportsError()
    {
        new LayerStorageMapping(TableName: "f", StorageSrid: 0).Validate()
            .Should().Contain(e => e.Contains("SRID", System.StringComparison.OrdinalIgnoreCase));
        new LayerStorageMapping(TableName: "f", StorageSrid: -1).Validate()
            .Should().Contain(e => e.Contains("SRID", System.StringComparison.OrdinalIgnoreCase));
    }

    [UnitTest]
    public void Validate_NullSrid_DoesNotReportError()
    {
        new LayerStorageMapping(TableName: "f", StorageSrid: null).Validate().Should().BeEmpty();
    }

    [UnitTest]
    public void QualifiedName_OnlyTableName_RendersTableOnly()
    {
        new LayerStorageMapping(TableName: "features").QualifiedName.Should().Be("features");
    }

    [UnitTest]
    public void QualifiedName_WithSchema_RendersSchemaDotTable()
    {
        new LayerStorageMapping(TableName: "features", SchemaName: "public").QualifiedName
            .Should().Be("public.features");
    }

    [UnitTest]
    public void QualifiedName_AllQualifiers_ConcatenatesWithDots()
    {
        var mapping = new LayerStorageMapping(
            TableName: "features",
            SchemaName: "public",
            CatalogName: "cat",
            DatabaseName: "db");

        mapping.QualifiedName.Should().Be("db.cat.public.features");
    }

    [UnitTest]
    public void QualifiedName_SkipsBlankQualifiers()
    {
        var mapping = new LayerStorageMapping(
            TableName: "features",
            SchemaName: "public",
            CatalogName: "   ",
            DatabaseName: "db");

        mapping.QualifiedName.Should().Be("db.public.features");
    }

    [UnitTest]
    public void IsSourceBacked_TrueOptionValue_ReportsTrue()
    {
        var mapping = new LayerStorageMapping(
            TableName: "features",
            ProviderOptions: new Dictionary<string, string>
            {
                [LayerStorageMapping.SourceBackedOption] = "true",
            });

        mapping.IsSourceBacked.Should().BeTrue();
    }

    [UnitTest]
    public void IsSourceBacked_NonBoolValue_ReportsFalse()
    {
        var mapping = new LayerStorageMapping(
            TableName: "features",
            ProviderOptions: new Dictionary<string, string>
            {
                [LayerStorageMapping.SourceBackedOption] = "yes",
            });

        mapping.IsSourceBacked.Should().BeFalse();
    }

    [UnitTest]
    public void IsSourceBacked_FalseOptionValue_ReportsFalse()
    {
        var mapping = new LayerStorageMapping(
            TableName: "features",
            ProviderOptions: new Dictionary<string, string>
            {
                [LayerStorageMapping.SourceBackedOption] = "false",
            });

        mapping.IsSourceBacked.Should().BeFalse();
    }

    [UnitTest]
    public void SourceBackedOption_ConstantIsStable()
    {
        // The provider-option key is part of the storage-mapping contract.
        LayerStorageMapping.SourceBackedOption.Should().Be("sourceBacked");
    }
}

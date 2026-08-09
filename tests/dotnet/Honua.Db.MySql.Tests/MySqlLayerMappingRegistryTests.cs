// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.MySql.Features.Infrastructure;

namespace Honua.MySql.Tests;

/// <summary>
/// Unit tests for <see cref="MySqlLayerMappingRegistry"/> behaviour:
/// resolution, missing-layer rejection, qualified table SQL formatting.
/// </summary>
public class MySqlLayerMappingRegistryTests
{
    [Fact]
    public void GetRequiredMapping_KnownLayer_ReturnsMapping()
    {
        var mapping = new MySqlLayerMapping
        {
            LayerId = 1,
            TableName = "parcels",
            GeometryColumn = "geom",
            PrimaryKeyColumn = "id",
            Srid = 4326,
            AttributeColumns = ["name"],
            GeometryType = GeometryType.Polygon
        };

        var registry = new MySqlLayerMappingRegistry([mapping]);

        Assert.Same(mapping, registry.GetRequiredMapping(1));
    }

    [Fact]
    public void GetRequiredMapping_UnknownLayer_Throws()
    {
        var registry = new MySqlLayerMappingRegistry([]);

        Assert.Throws<KeyNotFoundException>(() => registry.GetRequiredMapping(99));
    }

    [Fact]
    public void GetMapping_UnknownLayer_ReturnsNull()
    {
        var registry = new MySqlLayerMappingRegistry([]);

        Assert.Null(registry.GetMapping(99));
    }

    [Fact]
    public void QualifiedTableSql_WithSchema_BackticksBoth()
    {
        var mapping = new MySqlLayerMapping
        {
            LayerId = 1,
            TableName = "parcels",
            SchemaName = "honua",
            GeometryColumn = "geom",
            PrimaryKeyColumn = "id",
            AttributeColumns = []
        };

        Assert.Equal("`honua`.`parcels`", mapping.QualifiedTableSql);
        Assert.Equal("`geom`", mapping.QuotedGeometryColumn);
        Assert.Equal("`id`", mapping.QuotedPrimaryKeyColumn);
    }

    [Fact]
    public void QualifiedTableSql_WithoutSchema_OmitsQualifier()
    {
        var mapping = new MySqlLayerMapping
        {
            LayerId = 1,
            TableName = "parcels",
            GeometryColumn = "geom",
            PrimaryKeyColumn = "id",
            AttributeColumns = []
        };

        Assert.Equal("`parcels`", mapping.QualifiedTableSql);
    }

    [Fact]
    public void Quote_EscapesEmbeddedBackticks()
    {
        Assert.Equal("`weird``name`", MySqlIdentifier.Quote("weird`name"));
    }

    [Theory]
    [InlineData("valid_name")]
    [InlineData("ValidName")]
    [InlineData("valid123")]
    public void ValidateFieldName_AcceptsSimpleIdentifiers(string name)
    {
        var ex = Record.Exception(() => MySqlIdentifier.ValidateFieldName(name));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1invalid")]
    [InlineData("with space")]
    [InlineData("`drop`")]
    [InlineData("'inject")]
    public void ValidateFieldName_RejectsUnsafeIdentifiers(string name)
    {
        Assert.Throws<ArgumentException>(() => MySqlIdentifier.ValidateFieldName(name));
    }

    [Fact]
    public void Constructor_NullMappings_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new MySqlLayerMappingRegistry(null!));
    }
}

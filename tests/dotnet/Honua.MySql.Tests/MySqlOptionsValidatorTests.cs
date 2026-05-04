// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.MySql.Tests;

/// <summary>
/// Validates the startup-time configuration validator surfaces meaningful errors
/// for missing connection strings, missing layers, and incomplete layer mappings.
/// </summary>
public class MySqlOptionsValidatorTests
{
    [Fact]
    public void ThrowIfInvalid_MissingConnectionString_Throws()
    {
        var options = new MySqlOptions
        {
            Layers = [BuildLayer()]
        };

        var ex = Assert.Throws<InvalidOperationException>(() => MySqlOptionsValidator.ThrowIfInvalid(options));
        Assert.Contains("ConnectionString", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfInvalid_NoLayers_Throws()
    {
        var options = new MySqlOptions { ConnectionString = "Server=localhost;" };

        var ex = Assert.Throws<InvalidOperationException>(() => MySqlOptionsValidator.ThrowIfInvalid(options));
        Assert.Contains("Layers", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfInvalid_DuplicateLayerIds_Throws()
    {
        var options = new MySqlOptions
        {
            ConnectionString = "Server=localhost;",
            Layers = [BuildLayer(id: 1), BuildLayer(id: 1, table: "other")]
        };

        var ex = Assert.Throws<InvalidOperationException>(() => MySqlOptionsValidator.ThrowIfInvalid(options));
        Assert.Contains("duplicated", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfInvalid_MissingAttributes_Throws()
    {
        var options = new MySqlOptions
        {
            ConnectionString = "Server=localhost;",
            Layers = [BuildLayer(attributes: [])]
        };

        var ex = Assert.Throws<InvalidOperationException>(() => MySqlOptionsValidator.ThrowIfInvalid(options));
        Assert.Contains("Attributes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfInvalid_ValidConfiguration_DoesNotThrow()
    {
        var options = new MySqlOptions
        {
            ConnectionString = "Server=localhost;",
            Layers = [BuildLayer()]
        };

        var ex = Record.Exception(() => MySqlOptionsValidator.ThrowIfInvalid(options));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData("Pont")]      // typo for "Point"
    [InlineData("polygons")]  // plural typo
    [InlineData("Curve")]     // unsupported type
    [InlineData("xyz")]
    public void ThrowIfInvalid_InvalidGeometryType_Throws(string geometryType)
    {
        // A typo in GeometryType used to silently fall back to Point at mapping-build
        // time, allowing point-only SQL paths (distance filters) to run against non-point
        // data. The validator now rejects unknown values up front.
        var options = new MySqlOptions
        {
            ConnectionString = "Server=localhost;",
            Layers = [BuildLayer(geometryType: geometryType)]
        };

        var ex = Assert.Throws<InvalidOperationException>(() => MySqlOptionsValidator.ThrowIfInvalid(options));
        Assert.Contains("GeometryType", ex.Message, StringComparison.Ordinal);
        Assert.Contains(geometryType, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Point")]
    [InlineData("point")]
    [InlineData("MULTIPOINT")]
    [InlineData("LineString")]
    [InlineData("Polygon")]
    [InlineData("MultiPolygon")]
    [InlineData("GeometryCollection")]
    [InlineData("None")]
    public void ThrowIfInvalid_ValidGeometryType_DoesNotThrow(string geometryType)
    {
        var options = new MySqlOptions
        {
            ConnectionString = "Server=localhost;",
            Layers = [BuildLayer(geometryType: geometryType)]
        };

        var ex = Record.Exception(() => MySqlOptionsValidator.ThrowIfInvalid(options));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData("postgres")]    // wrong engine entirely
    [InlineData("mssql")]
    [InlineData("xyz")]
    public void ThrowIfInvalid_InvalidEngineFlavor_Throws(string flavor)
    {
        // EngineFlavor controls per-engine WKB axis-order handling. An unknown value would
        // cause the parser fallback to silently pick Mysql and emit a 3-arg ST_GeomFromWKB
        // on MariaDB, which fails at the first spatial query. Reject it up front.
        var options = new MySqlOptions
        {
            ConnectionString = "Server=localhost;",
            EngineFlavor = flavor,
            Layers = [BuildLayer()]
        };

        var ex = Assert.Throws<InvalidOperationException>(() => MySqlOptionsValidator.ThrowIfInvalid(options));
        Assert.Contains("EngineFlavor", ex.Message, StringComparison.Ordinal);
        Assert.Contains(flavor, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Mysql")]
    [InlineData("mysql")]
    [InlineData("MariaDb")]
    [InlineData("mariadb")]
    public void ThrowIfInvalid_ValidEngineFlavor_DoesNotThrow(string flavor)
    {
        var options = new MySqlOptions
        {
            ConnectionString = "Server=localhost;",
            EngineFlavor = flavor,
            Layers = [BuildLayer()]
        };

        var ex = Record.Exception(() => MySqlOptionsValidator.ThrowIfInvalid(options));
        Assert.Null(ex);
    }

    private static MySqlLayerOptions BuildLayer(
        int id = 1, string name = "Parcels", string table = "parcels", string[]? attributes = null,
        string geometryType = "Polygon")
        => new()
        {
            Id = id,
            Name = name,
            Table = table,
            GeometryColumn = "geom",
            PrimaryKeyColumn = "id",
            Srid = 4326,
            GeometryType = geometryType,
            Attributes = attributes ?? ["name"]
        };
}

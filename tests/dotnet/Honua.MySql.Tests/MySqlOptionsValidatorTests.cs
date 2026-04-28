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

    private static MySqlLayerOptions BuildLayer(
        int id = 1, string name = "Parcels", string table = "parcels", string[]? attributes = null)
        => new()
        {
            Id = id,
            Name = name,
            Table = table,
            GeometryColumn = "geom",
            PrimaryKeyColumn = "id",
            Srid = 4326,
            GeometryType = "Polygon",
            Attributes = attributes ?? ["name"]
        };
}

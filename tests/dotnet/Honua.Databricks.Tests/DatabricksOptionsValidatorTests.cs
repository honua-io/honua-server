// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Databricks.Tests;

public class DatabricksOptionsValidatorTests
{
    private static DatabricksOptions Valid() => new()
    {
        Host = "https://example.cloud.databricks.com",
        WarehouseId = "wh123",
        Token = "token",
    };

    [Fact]
    public void ThrowIfInvalid_ValidOptions_DoesNotThrow()
    {
        DatabricksOptionsValidator.ThrowIfInvalid(Valid());
    }

    [Fact]
    public void ThrowIfInvalid_Disabled_SkipsValidation()
    {
        // A disabled provider may leave the section blank.
        DatabricksOptionsValidator.ThrowIfInvalid(new DatabricksOptions { Enabled = false });
    }

    [Theory]
    [InlineData("ftp://nope")]
    [InlineData("http://insecure.example.com")]
    public void ThrowIfInvalid_NonHttpsHost_Throws(string host)
    {
        var options = Valid();
        options.Host = host;
        Assert.Throws<InvalidOperationException>(() => DatabricksOptionsValidator.ThrowIfInvalid(options));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ThrowIfInvalid_EnabledButNoHost_StaysDormant(string? host)
    {
        // An optional read-through provider is enabled by default but must stay dormant
        // (and never block host startup) until an operator supplies Databricks:Host —
        // matching the SQL Server / Oracle / Redshift secondary-provider contract.
        var options = new DatabricksOptions { Enabled = true, Host = host! };
        DatabricksOptionsValidator.ThrowIfInvalid(options);
    }

    [Fact]
    public void ThrowIfInvalid_MissingWarehouseId_Throws()
    {
        var options = Valid();
        options.WarehouseId = "";
        Assert.Throws<InvalidOperationException>(() => DatabricksOptionsValidator.ThrowIfInvalid(options));
    }

    [Fact]
    public void ThrowIfInvalid_MissingToken_Throws()
    {
        var options = Valid();
        options.Token = "";
        Assert.Throws<InvalidOperationException>(() => DatabricksOptionsValidator.ThrowIfInvalid(options));
    }

    [Fact]
    public void ThrowIfInvalid_InvalidLayerIdentifier_Throws()
    {
        var options = Valid();
        options.Layers = [new DatabricksLayerOptions { Id = 1, Table = "bad name; DROP", GeometryColumn = "geom", PrimaryKeyColumn = "id" }];
        Assert.Throws<ArgumentException>(() => DatabricksOptionsValidator.ThrowIfInvalid(options));
    }

    [Fact]
    public void ThrowIfInvalid_UnknownGeometryType_Throws()
    {
        var options = Valid();
        options.Layers = [new DatabricksLayerOptions { Id = 1, Table = "t", GeometryColumn = "geom", PrimaryKeyColumn = "id", GeometryType = "Hyperbola" }];
        Assert.Throws<InvalidOperationException>(() => DatabricksOptionsValidator.ThrowIfInvalid(options));
    }

    [Fact]
    public void ThrowIfInvalid_NegativeMaxRetryAttempts_Throws()
    {
        var options = Valid();
        options.MaxRetryAttempts = -1;
        Assert.Throws<InvalidOperationException>(() => DatabricksOptionsValidator.ThrowIfInvalid(options));
    }

    [Fact]
    public void ThrowIfInvalid_ZeroMaxRetryAttempts_DoesNotThrow()
    {
        // Zero is valid and disables retries.
        var options = Valid();
        options.MaxRetryAttempts = 0;
        DatabricksOptionsValidator.ThrowIfInvalid(options);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void ThrowIfInvalid_NonPositiveRetryBaseDelay_Throws(int delayMs)
    {
        var options = Valid();
        options.RetryBaseDelayMilliseconds = delayMs;
        Assert.Throws<InvalidOperationException>(() => DatabricksOptionsValidator.ThrowIfInvalid(options));
    }
}

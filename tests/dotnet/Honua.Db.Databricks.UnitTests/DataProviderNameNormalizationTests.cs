// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Db.Databricks.Tests;

[global::Xunit.Trait("Tier", "Fast")]
public class DataProviderNameNormalizationTests
{
    [Theory]
    [InlineData("databricks")]
    [InlineData("Databricks")]
    [InlineData("DATABRICKS")]
    [InlineData("databrickssql")]
    [InlineData("databricks_sql")]
    [InlineData("dbsql")]
    public void Normalize_AllAliases_ResolveToCanonicalDatabricks(string provider)
    {
        Assert.Equal(DataProviderNames.Databricks, DataProviderNames.Normalize(provider));
    }

    [Fact]
    public void Databricks_CanonicalName_IsLowercase()
    {
        Assert.Equal("databricks", DataProviderNames.Databricks);
    }
}

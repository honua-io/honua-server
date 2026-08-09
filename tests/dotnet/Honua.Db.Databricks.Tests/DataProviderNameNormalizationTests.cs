// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Databricks.Tests;

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

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Startup;

public sealed class DataStoreBuildPolicyTests
{
    [UnitTheory]
    [InlineData("duckdb")]
    [InlineData("mariadb")]
    [InlineData("mssql")]
    [InlineData("oracledb")]
    [InlineData("redshift")]
    [InlineData("snowflakedb")]
    [InlineData("dbsql")]
    public void Validate_ExcludedPrimaryProvider_RefusesStartupAndNamesFullVariant(string provider)
    {
        var configuration = Configuration(("DataSource:Provider", provider));

        var start = () => DataStoreBuildPolicy.Validate(configuration, additionalStoresIncluded: false);

        start.Should().Throw<InvalidOperationException>().WithMessage("*PostGIS*-full*");
    }

    [UnitTheory]
    [InlineData("SqlServer:ConnectionString", "configured")]
    [InlineData("Oracle:Enabled", "true")]
    [InlineData("MySql:ConnectionString", "configured")]
    [InlineData("DuckDB:DatabasePath", "configured")]
    [InlineData("Redshift:Enabled", "true")]
    [InlineData("Snowflake:Account", "configured")]
    [InlineData("Databricks:Host", "configured")]
    [InlineData("GeoServicesRest:Enabled", "true")]
    [InlineData("Experimental:Features:RedshiftProvider", "true")]
    [InlineData("Experimental:Features:SnowflakeProvider", "true")]
    [InlineData("Experimental:Features:DatabricksProvider", "true")]
    public void Validate_ExcludedSecondaryProvider_RefusesStartupWithoutExposingSettings(string key, string value)
    {
        var configuration = Configuration((key, value));

        var start = () => DataStoreBuildPolicy.Validate(configuration, additionalStoresIncluded: false);

        start.Should().Throw<InvalidOperationException>().WithMessage("*-full*")
            .Which.Message.Should().NotContain(value);
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("postgis")]
    [InlineData("postgresql")]
    [InlineData("pgsql")]
    public void Validate_PostgisOnly_Accepts(string? provider)
    {
        var configuration = Configuration(("DataSource:Provider", provider));

        var start = () => DataStoreBuildPolicy.Validate(configuration, additionalStoresIncluded: false);

        start.Should().NotThrow();
    }

    [UnitTest]
    public void Validate_ExplicitlyDisabledStore_AcceptsDormantConfiguration()
    {
        var configuration = Configuration(("SqlServer:Enabled", "false"), ("SqlServer:ConnectionString", "configured"));

        var start = () => DataStoreBuildPolicy.Validate(configuration, additionalStoresIncluded: false);

        start.Should().NotThrow();
    }

    [UnitTest]
    public void Validate_FullBuild_AcceptsAdditionalStoreConfiguration()
    {
        var configuration = Configuration(("DataSource:Provider", "duckdb"), ("Oracle:Enabled", "true"));

        var start = () => DataStoreBuildPolicy.Validate(configuration, additionalStoresIncluded: true);

        start.Should().NotThrow();
    }

    private static IConfiguration Configuration(params (string Key, string? Value)[] settings)
        => new ConfigurationBuilder().AddInMemoryCollection(
            settings.Select(setting => KeyValuePair.Create(setting.Key, setting.Value))).Build();
}

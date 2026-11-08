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
    [InlineData("featureserver")]
    [InlineData("unknown-provider")]
    public void ValidatePostgisConfiguration_ExcludedPrimaryProvider_RefusesStartupAndNamesFullVariant(string provider)
    {
        var configuration = Configuration(("DataSource:Provider", provider));

        var start = () => DataStoreBuildPolicy.ValidatePostgisConfiguration(configuration);

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
    public void ValidatePostgisConfiguration_ExcludedSecondaryProvider_RefusesStartupWithoutExposingSettings(string key, string value)
    {
        var configuration = Configuration((key, value));

        var start = () => DataStoreBuildPolicy.ValidatePostgisConfiguration(configuration);

        start.Should().Throw<InvalidOperationException>().WithMessage("*-full*")
            .Which.Message.Should().NotContain(value);
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("postgis")]
    [InlineData("postgresql")]
    [InlineData("pgsql")]
    [InlineData("postgres")]
    [InlineData(" POST_GIS ")]
    public void ValidatePostgisConfiguration_PostgisOnly_Accepts(string? provider)
    {
        var configuration = Configuration(("DataSource:Provider", provider));

        var start = () => DataStoreBuildPolicy.ValidatePostgisConfiguration(configuration);

        start.Should().NotThrow();
    }

    [UnitTheory]
    [InlineData("DuckDB")]
    [InlineData("MySql")]
    [InlineData("SqlServer")]
    [InlineData("Oracle")]
    [InlineData("GeoServicesRest")]
    [InlineData("Redshift")]
    [InlineData("Snowflake")]
    [InlineData("Databricks")]
    public void ValidatePostgisConfiguration_ExplicitlyDisabledStore_AcceptsDormantConfiguration(string section)
    {
        var configuration = Configuration(($"{section}:Enabled", "false"), ($"{section}:ConnectionString", "configured"));

        var start = () => DataStoreBuildPolicy.ValidatePostgisConfiguration(configuration);

        start.Should().NotThrow();
    }

    [UnitTest]
    public void ValidatePostgisConfiguration_DisabledWarehouseGates_Accepts()
    {
        var configuration = Configuration(("Experimental:Features:RedshiftProvider", "false"),
            ("Experimental:Features:SnowflakeProvider", "false"),
            ("Experimental:Features:DatabricksProvider", "false"));

        var start = () => DataStoreBuildPolicy.ValidatePostgisConfiguration(configuration);

        start.Should().NotThrow();
    }

    [UnitTest]
    public void Validate_FullBuild_AcceptsAdditionalStoreConfiguration()
    {
        var configuration = Configuration(("DataSource:Provider", "duckdb"), ("Oracle:Enabled", "true"));

        var start = () => DataStoreBuildPolicy.Validate(configuration);

        start.Should().NotThrow();
    }

    private static IConfiguration Configuration(params (string Key, string? Value)[] settings)
        => new ConfigurationBuilder().AddInMemoryCollection(
            settings.Select(setting => KeyValuePair.Create(setting.Key, setting.Value))).Build();
}

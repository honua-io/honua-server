// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Honua.Server.Tests.Infrastructure;

/// <summary>
/// Tests for <see cref="PostgresDataSourceFactory"/> multiplexing resolution,
/// connection pool default tuning, and session-settings delivery (physical-connection
/// initializer vs. the libpq <c>options</c> startup parameter, which AWS RDS Proxy and
/// transaction-mode poolers reject — issue #1638).
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class PostgresDataSourceFactoryTests
{
    private const string TestConnectionString = "Host=localhost;Database=honua;Username=honua";

    [global::Xunit.Trait("Tier", "Fast")]
    [Theory]
    [InlineData("false", false, false)]
    [InlineData("true", false, true)]
    [InlineData("auto", false, true)]
    [InlineData("FALSE", false, false)]
    [InlineData("True", false, true)]
    [InlineData("AUTO", false, true)]
    [InlineData(null, false, false)]
    [InlineData("", false, false)]
    [InlineData("   ", false, false)]
    // Unrecognized values must default to the safe off behavior so a typo
    // cannot silently flip multiplexing on — paired with LimitsOptionsValidator
    // rejection for fail-fast startup semantics.
    [InlineData("fasle", false, false)]
    [InlineData("yes", false, false)]
    [InlineData("false", true, false)]
    [InlineData("true", true, false)]
    [InlineData("auto", true, false)]
    [Operation(Operations.TestInfrastructure)]
    public void ResolveMultiplexing_ResolvesCorrectly(string? setting, bool schemaHeaders, bool expected)
    {
        var result = PostgresDataSourceFactory.ResolveMultiplexing(setting, schemaHeaders);

        result.Should().Be(expected,
            $"Multiplexing='{setting}', schemaHeaders={schemaHeaders} should resolve to {expected}");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ConnectionLimits_Defaults_OptimizedForHighConcurrency()
    {
        var limits = new ConnectionLimits();

        limits.MaxConnectionPoolSize.Should().Be(200);
        limits.MinConnectionPoolSize.Should().Be(20);
        limits.BufferSizeBytes.Should().Be(32768);
        limits.Multiplexing.Should().Be("false");
        limits.ConnectionAcquisitionTimeoutSeconds.Should().Be(5);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ResolveConnectionLimits_LambdaHost_DoesNotPinThePoolAtTheCap()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AWS_LAMBDA_FUNCTION_NAME"] = "honua",
                ["Limits:Connections:MaxConnectionPoolSize"] = "8",
            })
            .Build();

        var limits = PostgresDataSourceFactory.ResolveConnectionLimits(configuration);

        limits.MaxConnectionPoolSize.Should().Be(8);
        limits.MaxConcurrentQueries.Should().Be(PostgresDataSourceFactory.LambdaUnsetMaxConnectionPoolSize,
            "an unset query ceiling on Lambda must not stay at the long-lived default of 200");
        limits.MinConnectionPoolSize.Should().Be(0,
            "a frozen Lambda does not prune, so the long-lived floor of 20 must not clamp up to the cap");
        limits.ConnectionIdleLifetimeSeconds.Should().Be(PostgresDataSourceFactory.LambdaIdleConnectionLifetimeSeconds);

        var builder = new NpgsqlDataSourceBuilder(TestConnectionString);
        PostgresDataSourceFactory.Configure(builder, schemaHeadersEnabled: false, limits);
        builder.ConnectionStringBuilder.MinPoolSize.Should().Be(0);
        builder.ConnectionStringBuilder.MaxPoolSize.Should().Be(8);
        builder.ConnectionStringBuilder.ConnectionIdleLifetime.Should().Be(60);
        builder.ConnectionStringBuilder.Pooling.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ResolveConnectionLimits_LambdaHost_BoundsConnectionLifetimeBelowProxyIdleTimeout()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AWS_LAMBDA_FUNCTION_NAME"] = "honua",
                ["Limits:Connections:ConnectionIdleLifetimeSeconds"] = "60",
            })
            .Build();

        var limits = PostgresDataSourceFactory.ResolveConnectionLimits(configuration);

        limits.ConnectionLifetimeSeconds.Should().Be(PostgresDataSourceFactory.LambdaConnectionLifetimeSeconds);
        limits.ConnectionLifetimeSeconds.Should().BeLessThan(60,
            "a pooled connection must be retired before the 60 s RDS Proxy idle-client timeout closes it");

        var builder = new NpgsqlDataSourceBuilder(TestConnectionString);
        PostgresDataSourceFactory.Configure(builder, schemaHeadersEnabled: false, limits);
        builder.ConnectionStringBuilder.ConnectionLifetime.Should().Be(PostgresDataSourceFactory.LambdaConnectionLifetimeSeconds);

        using var bound = PostgresDataSourceFactory.CreateForBoundSource(TestConnectionString, limits);
        new NpgsqlConnectionStringBuilder(bound.ConnectionString).ConnectionLifetime
            .Should().Be(PostgresDataSourceFactory.LambdaConnectionLifetimeSeconds,
                "bound sources (the publish path's source database) go through the same proxy");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ResolveConnectionLimits_ExplicitConnectionLifetime_Wins()
    {
        var lambda = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AWS_LAMBDA_FUNCTION_NAME"] = "honua",
                ["Limits:Connections:ConnectionLifetimeSeconds"] = "20",
            })
            .Build();
        PostgresDataSourceFactory.ResolveConnectionLimits(lambda).ConnectionLifetimeSeconds.Should().Be(20);

        var longLived = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Limits:Connections:ConnectionLifetimeSeconds"] = "300",
            })
            .Build();
        var limits = PostgresDataSourceFactory.ResolveConnectionLimits(longLived);
        limits.ConnectionLifetimeSeconds.Should().Be(300);
        var builder = new NpgsqlDataSourceBuilder(TestConnectionString);
        PostgresDataSourceFactory.Configure(builder, schemaHeadersEnabled: false, limits);
        builder.ConnectionStringBuilder.ConnectionLifetime.Should().Be(300);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ResolveConnectionLimits_EventHandlerLambda_ExplicitPoolFloorWins()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HONUA_CONTROL_PLANE_LAMBDA_HANDLER"] = "scheduled-tick",
                ["Limits:Connections:MaxConnectionPoolSize"] = "2",
                ["Limits:Connections:MinConnectionPoolSize"] = "1",
                ["Limits:Connections:ConnectionIdleLifetimeSeconds"] = "30",
            })
            .Build();

        var limits = PostgresDataSourceFactory.ResolveConnectionLimits(configuration);

        limits.MinConnectionPoolSize.Should().Be(1);
        limits.ConnectionIdleLifetimeSeconds.Should().Be(30);
        limits.MaxConnectionPoolSize.Should().Be(2);
        limits.MaxConcurrentQueries.Should().Be(PostgresDataSourceFactory.LambdaUnsetMaxConnectionPoolSize);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ResolveConnectionLimits_LambdaHost_UnsetPoolUsesServerlessCeiling()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AWS_LAMBDA_FUNCTION_NAME"] = "honua",
            })
            .Build();

        var limits = PostgresDataSourceFactory.ResolveConnectionLimits(configuration);

        limits.MaxConnectionPoolSize.Should().Be(PostgresDataSourceFactory.LambdaUnsetMaxConnectionPoolSize);
        limits.MaxConcurrentQueries.Should().Be(PostgresDataSourceFactory.LambdaUnsetMaxConnectionPoolSize);
        limits.MinConnectionPoolSize.Should().Be(0);

        var explicitQueries = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HONUA_CONTROL_PLANE_LAMBDA_HANDLER"] = "scheduled-tick",
                ["Limits:Connections:MaxConcurrentQueries"] = "3",
                ["Limits:Connections:MaxConnectionPoolSize"] = "3",
            })
            .Build();
        var kept = PostgresDataSourceFactory.ResolveConnectionLimits(explicitQueries);
        kept.MaxConcurrentQueries.Should().Be(3, "an explicit Lambda ceiling wins over the unset default");
        kept.MaxConnectionPoolSize.Should().Be(3);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ResolveConnectionLimits_NonLambda_KeepsTheLongLivedFloor()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var limits = PostgresDataSourceFactory.ResolveConnectionLimits(configuration);

        limits.MaxConnectionPoolSize.Should().Be(200);
        limits.MaxConcurrentQueries.Should().Be(200);
        limits.MinConnectionPoolSize.Should().Be(20);
        limits.ConnectionIdleLifetimeSeconds.Should().Be(600);
        limits.ConnectionLifetimeSeconds.Should().Be(0, "long-lived hosts keep Npgsql's unlimited lifetime");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Configure_DefaultNonMultiplexingPath_DoesNotUseOptionsStartupParameter()
    {
        var builder = new NpgsqlDataSourceBuilder(TestConnectionString);

        PostgresDataSourceFactory.Configure(builder, schemaHeadersEnabled: false, new ConnectionLimits(), defaultSchema: "tenant");

        builder.ConnectionStringBuilder.Options.Should().BeNullOrEmpty(
            "RDS Proxy rejects the libpq options startup parameter with 0A000, so the default path must not use it");
        builder.ConnectionStringBuilder.Multiplexing.Should().BeFalse();
        builder.ConnectionStringBuilder.NoResetOnClose.Should().BeTrue(
            "initializer-applied session settings must survive pool return");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Configure_ReleaseSessionOnClose_InitializerPathIsUnpooled()
    {
        var builder = new NpgsqlDataSourceBuilder(TestConnectionString);

        PostgresDataSourceFactory.Configure(
            builder, schemaHeadersEnabled: false, new ConnectionLimits(), releaseSessionOnClose: true);

        builder.ConnectionStringBuilder.Pooling.Should().BeFalse(
            "a pooled connection keeps its RDS Proxy pin through a Lambda freeze (#6760)");
        builder.ConnectionStringBuilder.MinPoolSize.Should().Be(0);
        builder.ConnectionStringBuilder.Options.Should().BeNullOrEmpty();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Configure_ReleaseSessionOnClose_RoleDefaultsStaysPooled()
    {
        var builder = new NpgsqlDataSourceBuilder(TestConnectionString);

        PostgresDataSourceFactory.Configure(
            builder, schemaHeadersEnabled: false, new ConnectionLimits(),
            sessionSettingsMode: DatabaseSessionSettingsMode.RoleDefaults, releaseSessionOnClose: true);

        builder.ConnectionStringBuilder.Pooling.Should().BeTrue("role-defaults issues no session SET, so nothing pins");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Configure_ReleaseSessionOnClose_MultiplexingStaysPooled()
    {
        var builder = new NpgsqlDataSourceBuilder(TestConnectionString);

        PostgresDataSourceFactory.Configure(
            builder, schemaHeadersEnabled: false, new ConnectionLimits { Multiplexing = "true" }, releaseSessionOnClose: true);

        builder.ConnectionStringBuilder.Multiplexing.Should().BeTrue();
        builder.ConnectionStringBuilder.Pooling.Should().BeTrue("Npgsql multiplexing requires pooling");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void CreateForBoundSource_ReleaseSessionOnClose_IsUnpooled()
    {
        using var pooled = PostgresDataSourceFactory.CreateForBoundSource(TestConnectionString, new ConnectionLimits());
        using var unpooled = PostgresDataSourceFactory.CreateForBoundSource(
            TestConnectionString, new ConnectionLimits(), releaseSessionOnClose: true);

        new NpgsqlConnectionStringBuilder(pooled.ConnectionString).Pooling.Should().BeTrue();
        new NpgsqlConnectionStringBuilder(unpooled.ConnectionString).Pooling.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Create_DefaultNonMultiplexingPath_ConnectionStringContainsNoOptions()
    {
        using var dataSource = PostgresDataSourceFactory.Create(
            TestConnectionString, schemaHeadersEnabled: false, new ConnectionLimits(), defaultSchema: "tenant");

        dataSource.ConnectionString.Should().NotContainEquivalentOf("options",
            "RDS Proxy and transaction-mode poolers reject the options startup parameter");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Create_ExposedConnectionString_RetainsPasswordForReconnectPaths()
    {
        // Regression guard for the FileImport-shard SASL failure (honua-server#1628 follow-up):
        // the post-import canonical-snapshot refresh and layer-publish materialize paths call
        // IDatabaseConnectionProvider.GetConnectionString() (which returns
        // NpgsqlDataSource.ConnectionString) and open a *fresh* NpgsqlConnection from it. Npgsql
        // strips the password by default (Persist Security Info=false), which made those fresh
        // connections fail SCRAM authentication with "No password has been provided but the
        // backend requires one (in SASL/SCRAM-SHA-256)". The factory sets PersistSecurityInfo so
        // the exposed string stays usable — the backend still requires the password (auth is not
        // weakened), it is simply no longer redacted from the string handed back to reconnect.
        const string withPassword = "Host=localhost;Database=honua;Username=honua;Password=s3cr3t";

        using var dataSource = PostgresDataSourceFactory.Create(
            withPassword, schemaHeadersEnabled: false, new ConnectionLimits());

        dataSource.ConnectionString.Should().Contain("Password=s3cr3t",
            "GetConnectionString() must return a connection string that can open a fresh connection under SCRAM auth");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Configure_MultiplexingEnabled_KeepsOptionsStartupParameter()
    {
        // Deliberate: with multiplexing, startup options are the only per-session-default
        // delivery that survives interleaved logical sessions; multiplexing is therefore
        // documented as mutually exclusive with RDS Proxy / transaction-mode poolers.
        var builder = new NpgsqlDataSourceBuilder(TestConnectionString);
        var limits = new ConnectionLimits { Multiplexing = "true" };

        PostgresDataSourceFactory.Configure(builder, schemaHeadersEnabled: false, limits, defaultSchema: "tenant");

        builder.ConnectionStringBuilder.Multiplexing.Should().BeTrue();
        builder.ConnectionStringBuilder.Options.Should().Be(
            "-c lock_timeout=30s -c statement_timeout=30s -c idle_in_transaction_session_timeout=60s -c search_path=\"tenant\",public");
    }

    [UnitTheory]
    [InlineData("false", false)]
    [InlineData("true", false)]
    [InlineData("false", true)]
    [Operation(Operations.TestInfrastructure)]
    public void Configure_RoleDefaultsMode_EmitsNoSessionSettingsStartupOptions(string multiplexing, bool schemaHeaders)
    {
        // role-defaults (honua-server#5870): no SET initializer and no options startup
        // parameter in any mode; the database role's defaults apply instead.
        var builder = new NpgsqlDataSourceBuilder(TestConnectionString);
        var limits = new ConnectionLimits { Multiplexing = multiplexing };

        PostgresDataSourceFactory.Configure(
            builder, schemaHeaders, limits, defaultSchema: "tenant", DatabaseSessionSettingsMode.RoleDefaults);

        builder.ConnectionStringBuilder.Options.Should().BeNullOrEmpty();
        builder.ConnectionStringBuilder.NoResetOnClose.Should().Be(!schemaHeaders,
            "role-defaults keeps the pool-return behaviour; without schema headers no DISCARD ALL is sent, which would also pin a proxied connection");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Configure_RoleDefaultsMode_KeepsCallerSuppliedOptions()
    {
        var builder = new NpgsqlDataSourceBuilder(TestConnectionString + ";Options=-c work_mem=8MB");

        PostgresDataSourceFactory.Configure(
            builder, schemaHeadersEnabled: false, new ConnectionLimits { Multiplexing = "true" }, sessionSettingsMode: DatabaseSessionSettingsMode.RoleDefaults);

        builder.ConnectionStringBuilder.Options.Should().Be("-c work_mem=8MB");
    }

    [UnitTheory]
    [InlineData(null, true)]
    [InlineData("connection", true)]
    [InlineData("role-defaults", false)]
    [InlineData("typo", true)]
    [Operation(Operations.TestInfrastructure)]
    public void Create_FromConfiguration_HonoursDatabaseSessionSettingsMode(string? mode, bool expectOptions)
    {
        // Multiplexing makes the connection-mode settings visible on the connection string,
        // so the configuration-driven mode is observable without a database.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Limits:Connections:Multiplexing"] = "true",
                ["Database:SessionSettings:Mode"] = mode,
            })
            .Build();

        using var dataSource = PostgresDataSourceFactory.Create(TestConnectionString, configuration, schemaHeadersEnabled: false);

        if (expectOptions)
        {
            dataSource.ConnectionString.Should().Contain("lock_timeout=30s");
        }
        else
        {
            dataSource.ConnectionString.Should().NotContainEquivalentOf("options");
        }
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Configure_SchemaHeadersEnabled_KeepsOptionsStartupParameterWithoutSearchPath()
    {
        // Schema-header (test isolation) mode resets session state on pool return
        // (NoResetOnClose=false), which would wipe initializer-applied SETs — so the
        // timeouts stay on the startup parameter, and search_path is owned per request.
        var builder = new NpgsqlDataSourceBuilder(TestConnectionString);

        PostgresDataSourceFactory.Configure(builder, schemaHeadersEnabled: true, new ConnectionLimits(), defaultSchema: "tenant");

        builder.ConnectionStringBuilder.NoResetOnClose.Should().BeFalse();
        builder.ConnectionStringBuilder.Options.Should().Be(
            "-c lock_timeout=30s -c statement_timeout=30s -c idle_in_transaction_session_timeout=60s");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void BuildSessionSettingsSql_IncludesTimeoutsAndQuotedSearchPath()
    {
        var limits = new ConnectionLimits
        {
            LockTimeout = TimeSpan.FromSeconds(11),
            StatementTimeout = TimeSpan.FromSeconds(22),
            IdleInTransactionTimeout = TimeSpan.FromSeconds(33),
        };

        var sql = PostgresDataSourceFactory.BuildSessionSettingsSql(limits, "tenant");

        sql.Should().Contain("SET lock_timeout = '11s';");
        sql.Should().Contain("SET statement_timeout = '22s';");
        sql.Should().Contain("SET idle_in_transaction_session_timeout = '33s';");
        sql.Should().Contain("SET search_path = \"tenant\", public;");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void BuildSessionSettingsSql_WithoutSchema_OmitsSearchPath()
    {
        var sql = PostgresDataSourceFactory.BuildSessionSettingsSql(new ConnectionLimits(), searchPathSchema: null);

        sql.Should().Contain("SET lock_timeout = '30s';");
        sql.Should().Contain("SET statement_timeout = '30s';");
        sql.Should().Contain("SET idle_in_transaction_session_timeout = '60s';");
        sql.Should().NotContain("search_path");
    }

    [global::Xunit.Trait("Tier", "Fast")]
    [Theory]
    // Valid identifiers pass through trimmed.
    [InlineData(false, "tenant", "tenant")]
    [InlineData(false, "Tenant_01", "Tenant_01")]
    // Schema-header mode owns search_path per request — never embed a default.
    [InlineData(true, "tenant", null)]
    // Null/blank values are ignored.
    [InlineData(false, null, null)]
    [InlineData(false, "", null)]
    [InlineData(false, "   ", null)]
    // Values failing the identifier allow-list are rejected (SQL-injection guard).
    [InlineData(false, "bad-schema", null)]
    [InlineData(false, "tenant\";DROP TABLE x;--", null)]
    [InlineData(false, "tenant,public", null)]
    [Operation(Operations.TestInfrastructure)]
    public void ResolveSearchPathSchema_AppliesIdentifierAllowList(bool schemaHeaders, string? schema, string? expected)
    {
        var result = PostgresDataSourceFactory.ResolveSearchPathSchema(schemaHeaders, schema);

        result.Should().Be(expected);
    }
}

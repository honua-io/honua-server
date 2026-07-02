// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Postgres.Features.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
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

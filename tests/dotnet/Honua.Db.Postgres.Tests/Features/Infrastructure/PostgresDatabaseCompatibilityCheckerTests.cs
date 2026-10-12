// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Infrastructure;

[Collection("Database")]
public class PostgresDatabaseCompatibilityCheckerTests
{
    private readonly PostgresFixture _fixture;
    private readonly PostgresDatabaseCompatibilityChecker _checker;

    public PostgresDatabaseCompatibilityCheckerTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _checker = new PostgresDatabaseCompatibilityChecker(
            NullLogger<PostgresDatabaseCompatibilityChecker>.Instance);
    }

    [Fact]
    public async Task CheckCompatibilityAsync_WhenPostGisInstalled_ReturnsCompatible()
    {
        var result = await _checker.CheckCompatibilityAsync(_fixture.ConnectionString);

        result.IsCompatible.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task CheckCompatibilityAsync_ReturnsEngineVersion()
    {
        var result = await _checker.CheckCompatibilityAsync(_fixture.ConnectionString);

        result.EngineVersion.Should().NotBeNullOrWhiteSpace();
        result.EngineVersion.Should().Contain("PostgreSQL");
    }

    [Fact]
    public async Task CheckCompatibilityAsync_ReturnsPostGisVersion()
    {
        var result = await _checker.CheckCompatibilityAsync(_fixture.ConnectionString);

        result.PostGisVersion.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CheckCompatibilityAsync_ReturnsInstalledExtensions()
    {
        var result = await _checker.CheckCompatibilityAsync(_fixture.ConnectionString);

        result.InstalledExtensions.Should().Contain("postgis");
        result.InstalledExtensions.Should().Contain("postgis_raster");
    }

    [Fact]
    public async Task CheckCompatibilityAsync_WithUnreachableServer_ReturnsTransientUnavailable()
    {
        // Nothing listens on this port: an outage, not an incompatible database. Startup must be
        // able to degrade on it instead of exiting.
        var result = await _checker.CheckCompatibilityAsync(
            "Host=localhost;Port=59999;Database=nonexistent;Username=nope;Password=nope;Timeout=2");

        result.IsCompatible.Should().BeFalse();
        result.IsTransientFailure.Should().BeTrue();
        result.FailureClass.Should().NotBeNullOrWhiteSpace();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        result.ErrorMessage.Should().NotContain("59999").And.NotContain("nope");
        result.EngineVersion.Should().Be("unknown");
        result.CauseClass.Should().Be(DatabasePreflightCauseClasses.ConnectionRefused);
    }

    [Fact]
    public async Task CheckCompatibilityAsync_WhenThePoolCannotLendAConnection_ReportsBorrowTimeoutNotIncompatible()
    {
        // honua-release run 38192851328 logged "Database compatibility check failed" 36 times while
        // SQLSTATE 53300 stayed at 0: the preflight could not borrow a connection. Hold the only
        // pooled connection so the probe waits out its borrow timeout (honua-server#5916).
        var builder = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            MaxPoolSize = 1,
            Timeout = 1,
            ApplicationName = "honua-preflight-borrow-" + Guid.NewGuid().ToString("N"),
        };
        var connectionString = builder.ConnectionString;
        await using var holder = new NpgsqlConnection(connectionString);
        await holder.OpenAsync();

        var result = await _checker.CheckCompatibilityAsync(connectionString);

        result.IsCompatible.Should().BeFalse();
        result.IsTransientFailure.Should().BeTrue("a connection that could not be borrowed degrades startup");
        result.CauseClass.Should().Be(DatabasePreflightCauseClasses.ConnectionBorrowTimeout);
        result.ErrorMessage.Should().Contain("connection-borrow-timeout")
            .And.NotContain("Database compatibility check failed");
    }

    [Fact]
    public async Task CheckCompatibilityAsync_WithWrongPassword_ReportsAuthAndDegrades()
    {
        var builder = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            Password = "definitely-not-the-password",
            Pooling = false,
        };

        var result = await _checker.CheckCompatibilityAsync(builder.ConnectionString);

        result.IsCompatible.Should().BeFalse();
        result.IsTransientFailure.Should().BeTrue("auth is a degradable cause class");
        result.CauseClass.Should().Be(DatabasePreflightCauseClasses.Auth);
        result.ErrorMessage.Should().NotContain("definitely-not-the-password");
    }
}

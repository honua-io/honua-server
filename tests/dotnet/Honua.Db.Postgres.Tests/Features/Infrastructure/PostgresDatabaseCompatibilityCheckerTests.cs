// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

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
    }
}

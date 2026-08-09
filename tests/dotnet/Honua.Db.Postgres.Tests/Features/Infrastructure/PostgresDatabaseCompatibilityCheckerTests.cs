// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Postgres.Features.Infrastructure;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Postgres.Tests.Features.Infrastructure;

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
    public async Task CheckCompatibilityAsync_WithInvalidConnectionString_ReturnsIncompatible()
    {
        var result = await _checker.CheckCompatibilityAsync(
            "Host=localhost;Port=59999;Database=nonexistent;Username=nope;Password=nope;Timeout=2");

        result.IsCompatible.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        result.EngineVersion.Should().Be("unknown");
    }
}

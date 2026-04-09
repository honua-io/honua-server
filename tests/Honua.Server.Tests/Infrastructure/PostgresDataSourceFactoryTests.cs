// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Postgres.Features.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Infrastructure;

/// <summary>
/// Tests for <see cref="PostgresDataSourceFactory"/> multiplexing resolution
/// and connection pool default tuning.
/// </summary>
[Protocol(Protocols.TestQuality)]
public sealed class PostgresDataSourceFactoryTests
{
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
}

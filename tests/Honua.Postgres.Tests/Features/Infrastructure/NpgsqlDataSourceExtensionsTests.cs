// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Infrastructure;

public sealed class NpgsqlDataSourceExtensionsTests
{
    [Fact]
    public async Task WarmupConnectionPoolAsync_NullDataSource_ThrowsArgumentNullException()
    {
        NpgsqlDataSource? dataSource = null;

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            dataSource!.WarmupConnectionPoolAsync());

        exception.ParamName.Should().Be("dataSource");
    }

    [Fact]
    public async Task WarmupConnectionPoolAsync_NegativeMinConnections_ThrowsArgumentOutOfRangeException()
    {
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Port=5432;Database=honua_test;Username=postgres;Password=postgres;Timeout=1;Command Timeout=1");

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            dataSource.WarmupConnectionPoolAsync(minConnections: -1));

        exception.ParamName.Should().Be("minConnections");
    }
}

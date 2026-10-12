// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Xunit;

namespace Honua.Protocols.OData.ComponentTests;

/// <summary>Candidate fixture qualification, separate from the HTTP parity measurement.</summary>
[Trait("Category", "FixtureLifecycle")]
[Trait("Tier", "Integration")]
public sealed class PostgresFixtureLifecycleTests
{
    [Fact]
    public async Task DisposingUninitializedFixture_DoesNotReleaseActiveBoundedDatabase()
    {
        var active = new PostgresFixture(boundedResources: true);
        try
        {
            await active.InitializeAsync();
            await using (var before = await active.DataSource.OpenConnectionAsync())
            {
                await using var command = before.CreateCommand();
                command.CommandText = "SELECT 1";
                (await command.ExecuteScalarAsync()).Should().Be(1);
            }

            var uninitialized = new PostgresFixture(boundedResources: true);
            await uninitialized.DisposeAsync();
            await uninitialized.DisposeAsync();

            // The shared pool itself must remain usable. A leaked reference decrement
            // disposes this data source even if an external database remains running.
            await using var after = await active.DataSource.OpenConnectionAsync();
            await using var retained = after.CreateCommand();
            retained.CommandText = "SELECT 1";
            (await retained.ExecuteScalarAsync()).Should().Be(1);
        }
        finally
        {
            await active.DisposeAsync();
        }
    }
}

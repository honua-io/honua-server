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
/// Integration tests proving that <see cref="PostgresDataSourceFactory"/> applies the
/// session settings (lock/statement/idle-in-transaction timeouts and default
/// search_path) via the physical-connection initializer — i.e. with plain SET
/// statements after connect instead of the libpq <c>options</c> startup parameter,
/// which AWS RDS Proxy rejects with 0A000 and transaction-mode poolers break on
/// (issue #1638) — and that the settings survive pool return.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.TestQuality)]
public sealed class PostgresDataSourceFactorySessionSettingsTests : IAsyncLifetime
{
    private readonly DatabaseFixtureAdapter _fixture;
    private string _schemaName = null!;

    public PostgresDataSourceFactorySessionSettingsTests(DatabaseFixtureAdapter fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        // Short label: the fixture appends "_<counter>_<32-char guid>" and the full
        // schema name must stay within PostgreSQL's 63-byte identifier limit, or the
        // server truncates it and the search_path assertions below fail.
        _schemaName = await _fixture.CreateIsolatedSchemaAsync("SessionSettings");
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropSchemaAsync(_schemaName);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Create_NonMultiplexing_AppliesSessionSettingsWithoutOptionsStartupParameter()
    {
        var limits = new ConnectionLimits
        {
            LockTimeout = TimeSpan.FromSeconds(17),
            StatementTimeout = TimeSpan.FromSeconds(43),
            IdleInTransactionTimeout = TimeSpan.FromSeconds(59),
            MinConnectionPoolSize = 0,
            MaxConnectionPoolSize = 2,
        };

        await using var dataSource = PostgresDataSourceFactory.Create(
            _fixture.ConnectionString, schemaHeadersEnabled: false, limits, defaultSchema: _schemaName);

        // The startup parameter that RDS Proxy rejects must not be present.
        dataSource.ConnectionString.Should().NotContainEquivalentOf("options");

        await using (var connection = await dataSource.OpenConnectionAsync())
        {
            (await ShowAsync(connection, "lock_timeout")).Should().Be("17s");
            (await ShowAsync(connection, "statement_timeout")).Should().Be("43s");
            (await ShowAsync(connection, "idle_in_transaction_session_timeout")).Should().Be("59s");
            (await ShowAsync(connection, "search_path")).Should().Contain(_schemaName).And.Contain("public");
        }

        // Reopen from the pool: NoResetOnClose=true must preserve the
        // initializer-applied settings across pool return, exactly as the old
        // startup-options approach did.
        await using (var connection = await dataSource.OpenConnectionAsync())
        {
            (await ShowAsync(connection, "lock_timeout")).Should().Be("17s");
            (await ShowAsync(connection, "statement_timeout")).Should().Be("43s");
            (await ShowAsync(connection, "idle_in_transaction_session_timeout")).Should().Be("59s");
            (await ShowAsync(connection, "search_path")).Should().Contain(_schemaName).And.Contain("public");
        }
    }

    private static async Task<string?> ShowAsync(NpgsqlConnection connection, string setting)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SHOW {setting}";
        return (string?)await command.ExecuteScalarAsync();
    }
}

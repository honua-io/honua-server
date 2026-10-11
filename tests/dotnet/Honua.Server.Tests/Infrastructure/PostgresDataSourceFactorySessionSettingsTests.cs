// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Health;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Server.Tests.Infrastructure;

/// <summary>
/// Integration tests proving that <see cref="PostgresDataSourceFactory"/> applies the
/// session settings (lock/statement/idle-in-transaction timeouts and default
/// search_path) via the physical-connection initializer — i.e. with plain SET
/// statements after connect instead of the libpq <c>options</c> startup parameter,
/// which AWS RDS Proxy rejects with 0A000 and transaction-mode poolers break on
/// (issue #1638) — and that the settings survive pool return. In
/// <c>Database:SessionSettings:Mode=role-defaults</c> the factory must issue no session
/// SET at all and the role defaults apply, verified at startup (issue #5870).
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

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Create_RoleDefaultsMode_IssuesNoSessionSetAndUsesRoleDefaults()
    {
        var limits = DistinctLimits();
        await using var role = await LoginRole.CreateAsync(_fixture);
        await role.SetDefaultsAsync("17s", "43s", "59s", $"\"{_schemaName}\", public");

        await using var dataSource = PostgresDataSourceFactory.Create(
            role.ConnectionString, schemaHeadersEnabled: false, limits, _schemaName, DatabaseSessionSettingsMode.RoleDefaults);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            // Second pass reuses the pooled physical connection.
            await using var connection = await dataSource.OpenConnectionAsync();
            (await ShowAsync(connection, "lock_timeout")).Should().Be("17s");
            (await ShowAsync(connection, "statement_timeout")).Should().Be("43s");
            (await ShowAsync(connection, "idle_in_transaction_session_timeout")).Should().Be("59s");
            (await ShowAsync(connection, "search_path")).Should().Contain(_schemaName);

            // 'user' = role default; a session SET would report 'session' and pin RDS Proxy.
            (await SourcesAsync(connection)).Should().AllBe("user");
        }
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Create_ConnectionMode_StillOverridesRoleDefaultsWithSessionSet()
    {
        var limits = DistinctLimits();
        await using var role = await LoginRole.CreateAsync(_fixture);
        await role.SetDefaultsAsync("17s", "43s", "59s", searchPath: null);

        await using var dataSource = PostgresDataSourceFactory.Create(
            role.ConnectionString, schemaHeadersEnabled: false, limits, _schemaName);

        await using var connection = await dataSource.OpenConnectionAsync();
        (await ShowAsync(connection, "lock_timeout")).Should().Be("5s");
        (await ShowAsync(connection, "statement_timeout")).Should().Be("6s");
        (await ShowAsync(connection, "idle_in_transaction_session_timeout")).Should().Be("7s");
        (await ShowAsync(connection, "search_path")).Should().Contain(_schemaName);
        (await SourcesAsync(connection)).Should().AllBe("session");
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task VerificationService_RoleDefaultsUnset_WarnsWithAlterRoleStatements()
    {
        await using var role = await LoginRole.CreateAsync(_fixture);
        await using var dataSource = PostgresDataSourceFactory.Create(
            role.ConnectionString, schemaHeadersEnabled: false, new ConnectionLimits(), _schemaName, DatabaseSessionSettingsMode.RoleDefaults);
        var state = new DatabaseSessionSettingsState(DatabaseSessionSettingsMode.RoleDefaults);
        var logger = new CapturingLogger<PostgresSessionSettingsVerificationService>();
        using var service = new PostgresSessionSettingsVerificationService(
            () => dataSource, new ConnectionLimits(), _schemaName, state, logger);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;

        state.Snapshot.Status.Should().Be(DatabaseSessionSettingsVerificationStatus.Mismatch);
        state.Snapshot.Role.Should().Be(role.Name);
        state.Snapshot.Drift.Select(drift => drift.Remediation).Should().Equal(
            $"ALTER ROLE \"{role.Name}\" SET lock_timeout = '30s';",
            $"ALTER ROLE \"{role.Name}\" SET statement_timeout = '30s';",
            $"ALTER ROLE \"{role.Name}\" SET idle_in_transaction_session_timeout = '60s';",
            $"ALTER ROLE \"{role.Name}\" SET search_path = \"{_schemaName}\", public;");
        var warnings = logger.Entries
            .Where(entry => entry.Level == LogLevel.Warning && entry.EventId == 5870)
            .Select(entry => entry.Message)
            .ToArray();
        warnings.Should().HaveCount(4);
        warnings.Should().Contain(message =>
            message.Contains($"ALTER ROLE \"{role.Name}\" SET lock_timeout = '30s';", StringComparison.Ordinal));

        // The operator runs exactly the reported statements; the next verification passes.
        foreach (var statement in state.Snapshot.Drift.Select(drift => drift.Remediation))
        {
            await _fixture.ExecuteAsync(statement);
        }

        dataSource.Clear();
        var verified = await PostgresSessionSettingsVerifier.VerifyAsync(
            dataSource, new ConnectionLimits(), _schemaName, CancellationToken.None);
        verified.Status.Should().Be(DatabaseSessionSettingsVerificationStatus.Verified);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task VerificationService_DatabaseUnreachable_ReportsUnavailableWithoutThrowing()
    {
        var unreachable = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            Host = "127.0.0.1",
            Port = 1,
            Timeout = 2,
        }.ConnectionString;
        await using var dataSource = PostgresDataSourceFactory.Create(
            unreachable, schemaHeadersEnabled: false, new ConnectionLimits(), sessionSettingsMode: DatabaseSessionSettingsMode.RoleDefaults);
        var state = new DatabaseSessionSettingsState(DatabaseSessionSettingsMode.RoleDefaults);
        var logger = new CapturingLogger<PostgresSessionSettingsVerificationService>();
        using var service = new PostgresSessionSettingsVerificationService(
            () => dataSource, new ConnectionLimits(), null, state, logger);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;

        state.Snapshot.Status.Should().Be(DatabaseSessionSettingsVerificationStatus.Unavailable);
        logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning && entry.EventId == 5872);
    }

    private static ConnectionLimits DistinctLimits() => new()
    {
        LockTimeout = TimeSpan.FromSeconds(5),
        StatementTimeout = TimeSpan.FromSeconds(6),
        IdleInTransactionTimeout = TimeSpan.FromSeconds(7),
        MinConnectionPoolSize = 0,
        MaxConnectionPoolSize = 1,
    };

    private static async Task<IReadOnlyList<string>> SourcesAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT source FROM pg_catalog.pg_settings WHERE name IN " +
            "('lock_timeout', 'statement_timeout', 'idle_in_transaction_session_timeout') ORDER BY name";
        var sources = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            sources.Add(reader.GetString(0));
        }

        sources.Should().HaveCount(3);
        return sources;
    }

    /// <summary>
    /// A throwaway LOGIN role (roles are cluster-wide, so each test owns a unique one).
    /// </summary>
    private sealed class LoginRole : IAsyncDisposable
    {
        private const string Password = "rd_test_pw";
        private readonly DatabaseFixtureAdapter _fixture;

        private LoginRole(DatabaseFixtureAdapter fixture, string name, string connectionString)
        {
            _fixture = fixture;
            Name = name;
            ConnectionString = connectionString;
        }

        public string Name { get; }

        public string ConnectionString { get; }

        public static async Task<LoginRole> CreateAsync(DatabaseFixtureAdapter fixture)
        {
            var name = $"honua_rd_{Guid.NewGuid():N}";
            await fixture.ExecuteAsync($"CREATE ROLE {name} LOGIN PASSWORD '{Password}'");
            var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                Username = name,
                Password = Password,
            }.ConnectionString;
            return new LoginRole(fixture, name, connectionString);
        }

        public Task SetDefaultsAsync(string lockTimeout, string statementTimeout, string idleTimeout, string? searchPath)
            => _fixture.ExecuteAsync(
                $"ALTER ROLE {Name} SET lock_timeout = '{lockTimeout}'; " +
                $"ALTER ROLE {Name} SET statement_timeout = '{statementTimeout}'; " +
                $"ALTER ROLE {Name} SET idle_in_transaction_session_timeout = '{idleTimeout}';" +
                (searchPath is null ? string.Empty : $" ALTER ROLE {Name} SET search_path = {searchPath};"));

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await _fixture.ExecuteAsync($"DROP ROLE IF EXISTS {Name}");
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, int EventId, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, int EventId, string Message)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToArray();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, eventId.Id, formatter(state, exception)));
            }
        }
    }

    private static async Task<string?> ShowAsync(NpgsqlConnection connection, string setting)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SHOW {setting}";
        return (string?)await command.ExecuteScalarAsync();
    }
}

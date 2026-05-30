// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using CoreSslMode = Honua.Core.Features.Security.Domain.SslMode;

namespace Honua.TestKit.Mixins;

/// <summary>
/// Audit-A2 mixin: the secure-connection bootstrap that <see cref="WebAppFixture"/>
/// historically inlined as ~130 LOC of <c>EnsureTestSecureConnectionAsync</c>,
/// <c>SecureConnectionTablesAvailableAsync</c>, <c>EnsureSecureConnectionProviderColumnAsync</c>,
/// and the transient-failure classifier. Registering the test secure connection has no
/// dependency on the fixture's other concerns (HTTP client, V2 graph, schema isolation),
/// so the whole block lives here as static helpers parameterised by the fixture's service
/// provider and the configuration's <c>DefaultConnection</c> string.
/// </summary>
/// <remarks>
/// Per structural-audit-2026-05 (group A2), this is the third slice of the WebAppFixture
/// mixin split. Behaviour is byte-identical to the previous inline implementations — this
/// is a pure relocation. The mixin owns a shared semaphore so that parallel fixtures
/// racing the same Postgres instance don't double-insert the connection.
/// </remarks>
internal static class WebAppFixtureSecureConnectionMixin
{
    /// <summary>
    /// Logical name of the test secure connection registered into the
    /// <see cref="ISecureConnectionRegistry"/>. Mirrors the historic constant on
    /// <see cref="WebAppFixture"/>.
    /// </summary>
    internal const string TestSecureConnectionName = "test";

    /// <summary>
    /// Audit trail tag written into the secure connection's <c>created_by</c> column.
    /// </summary>
    internal const string TestSecureConnectionCreatedBy = "test-fixture";

    private static readonly SemaphoreSlim _secureConnectionLock = new(1, 1);

    /// <summary>
    /// Returns the connection id of the registered test secure connection, or null when
    /// the service scope, registry, or row isn't available yet. Mirrors the historic
    /// behaviour of <see cref="WebAppFixture.GetTestSecureConnectionIdAsync"/>.
    /// </summary>
    internal static async Task<Guid?> GetTestSecureConnectionIdAsync(IServiceScope? scope)
    {
        if (scope is null)
        {
            return null;
        }

        var registry = scope.ServiceProvider.GetService<ISecureConnectionRegistry>();
        if (registry == null)
        {
            return null;
        }

        var connection = await registry.GetConnectionByNameAsync(TestSecureConnectionName).ConfigureAwait(false);
        return connection?.ConnectionId;
    }

    /// <summary>
    /// Ensures the test secure connection row exists in the live database. No-op if the
    /// fixture has no service scope, the secure-connections table is missing, or the
    /// connection string can't be parsed.
    /// </summary>
    internal static async Task EnsureTestSecureConnectionAsync(IServiceScope? scope)
    {
        if (scope is null)
        {
            return;
        }

        var services = scope.ServiceProvider;
        var connectionProvider = services.GetRequiredService<IDatabaseConnectionProvider>();

        if (!await SecureConnectionTablesAvailableAsync(connectionProvider).ConfigureAwait(false))
        {
            return;
        }

        await EnsureSecureConnectionProviderColumnAsync(connectionProvider).ConfigureAwait(false);

        var registry = services.GetRequiredService<ISecureConnectionRegistry>();
        var encryptionService = services.GetRequiredService<IConnectionEncryptionService>();
        var configuration = services.GetRequiredService<IConfiguration>();
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        await _secureConnectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var existing = await registry.GetConnectionByNameAsync(TestSecureConnectionName).ConfigureAwait(false);
            if (existing != null)
            {
                return;
            }

            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(builder.Host) ||
                string.IsNullOrWhiteSpace(builder.Database) ||
                string.IsNullOrWhiteSpace(builder.Username) ||
                builder.Port <= 0)
            {
                return;
            }

            var encrypted = await encryptionService.EncryptConnectionStringAsync(connectionString).ConfigureAwait(false);
            var keyVersion = await encryptionService.GetCurrentKeyVersionAsync().ConfigureAwait(false);
            var sslRequired = builder.SslMode is Npgsql.SslMode.Require or Npgsql.SslMode.VerifyCA or Npgsql.SslMode.VerifyFull;
            var sslMode = Enum.Parse<CoreSslMode>(builder.SslMode.ToString(), true);

            var connection = DataConnection.CreateWithEncryptedCredentials(
                name: TestSecureConnectionName,
                host: builder.Host,
                port: builder.Port,
                databaseName: builder.Database,
                username: builder.Username,
                encryptedConnectionString: encrypted,
                encryptionKeyVersion: keyVersion,
                createdBy: TestSecureConnectionCreatedBy,
                description: "Test secure connection",
                sslRequired: sslRequired,
                sslMode: sslMode);

            try
            {
                await registry.CreateConnectionAsync(connection).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (string.Equals(ex.SqlState, "23505", StringComparison.Ordinal))
            {
                // Another fixture created the same connection concurrently.
            }
        }
        finally
        {
            _secureConnectionLock.Release();
        }
    }

    private static async Task<bool> SecureConnectionTablesAvailableAsync(IDatabaseConnectionProvider connectionProvider)
    {
        const string sql = """
            SELECT 1
            FROM information_schema.tables
            WHERE table_schema = 'honua'
              AND table_name = 'data_connections'
            LIMIT 1
            """;
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await using var connection = await connectionProvider.OpenConnectionAsync().ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.CommandTimeout = 10;
                var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
                return result != null && result != DBNull.Value;
            }
            catch (Exception ex) when (IsTransientSecureConnectionCheckFailure(ex))
            {
                if (attempt == maxAttempts)
                {
                    Console.Error.WriteLine($"WARNING: Could not verify secure-connection table after {maxAttempts} attempts. Proceeding without it.");
                    return false;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt)).ConfigureAwait(false);
            }
        }

        Console.Error.WriteLine($"WARNING: Could not verify secure-connection table after {maxAttempts} attempts. Proceeding without it.");
        return false;
    }

    private static async Task EnsureSecureConnectionProviderColumnAsync(IDatabaseConnectionProvider connectionProvider)
    {
        const string sql = """
            ALTER TABLE IF EXISTS honua.data_connections
                ADD COLUMN IF NOT EXISTS provider_name TEXT NOT NULL DEFAULT 'postgis';
            """;

        await using var connection = await connectionProvider.OpenConnectionAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 10;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static bool IsTransientSecureConnectionCheckFailure(Exception ex)
    {
        return ex is TimeoutException or TaskCanceledException or NpgsqlException;
    }
}

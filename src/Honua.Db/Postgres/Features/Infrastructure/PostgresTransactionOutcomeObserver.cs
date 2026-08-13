// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Infrastructure.Abstractions;
using Npgsql;

namespace Honua.Db.Postgres.Features.Infrastructure;

/// <summary>
/// Captures and observes PostgreSQL's transaction-specific commit marker after an acknowledgement is
/// lost. Unlike probing a row touched by an idempotent mutation, the xid distinguishes this exact
/// transaction from state that was already present before it began.
/// </summary>
internal static class PostgresTransactionOutcomeObserver
{
    public static async Task<string> CaptureTransactionIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT pg_current_xact_id()::text;";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result as string
            ?? throw new InvalidOperationException("PostgreSQL did not return the current transaction id.");
    }

    public static async Task<bool?> TryObserveCommitAsync(
        IAdoNetDatabaseConnectionProvider connectionProvider,
        string transactionId)
        => await RetryUnresolvedObservationAsync(async () =>
        {
            try
            {
                await using var connection = await connectionProvider
                    .OpenNpgsqlConnectionAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                return await ObserveCommitAsync(connection, transactionId).ConfigureAwait(false);
            }
            catch (DbException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (TimeoutException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }).ConfigureAwait(false);

    public static async Task<bool?> TryObserveCommitAsync(
        string connectionString,
        string transactionId)
        => await RetryUnresolvedObservationAsync(async () =>
        {
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
                return await ObserveCommitAsync(connection, transactionId).ConfigureAwait(false);
            }
            catch (DbException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (TimeoutException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }).ConfigureAwait(false);

    internal static async Task<bool?> RetryUnresolvedObservationAsync(
        Func<Task<bool?>> observeAsync,
        Func<TimeSpan, Task>? delayAsync = null)
    {
        const int maxAttempts = 6;
        delayAsync ??= static delay => Task.Delay(delay, CancellationToken.None);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var observed = await observeAsync().ConfigureAwait(false);
            if (observed.HasValue || attempt == maxAttempts)
            {
                return observed;
            }

            var delayMilliseconds = Math.Min(100 * (1 << (attempt - 1)), 1_000);
            await delayAsync(TimeSpan.FromMilliseconds(delayMilliseconds)).ConfigureAwait(false);
        }

        return null;
    }

    internal static bool? InterpretStatus(string? status) => status switch
    {
        "committed" => true,
        "aborted" => false,
        _ => null,
    };

    private static async Task<bool?> ObserveCommitAsync(NpgsqlConnection connection, string transactionId)
    {
        const string sql = "SELECT pg_xact_status(@transactionId::xid8)::text;";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@transactionId", transactionId);
        var result = await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
        return InterpretStatus(result as string);
    }
}

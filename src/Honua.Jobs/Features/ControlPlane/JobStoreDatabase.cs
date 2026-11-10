// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Npgsql;

namespace Honua.ControlPlane;

// The system database as the job stores use it: schema-qualified table names and one place that
// decides when a failed statement is sent again.
internal sealed class JobStoreDatabase(NpgsqlDataSource dataSource, string? schemaName = null)
{
    internal const string DefaultSchemaName = "honua";

    private static readonly TimeSpan[] _transientRetryDelays =
    [
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2)
    ];

    private readonly string _quotedSchema = QuoteIdentifier(
        string.IsNullOrWhiteSpace(schemaName) ? DefaultSchemaName : schemaName);

    public string Table(string tableName) => $"{_quotedSchema}.{tableName}";

    internal static string QuoteIdentifier(string identifier)
    {
        using var builder = new NpgsqlCommandBuilder();
        return builder.QuoteIdentifier(identifier);
    }

    // Runs statement on a pooled connection. A transient failure to open the
    // connection is always retried. A transient failure after the statement was sent is retried
    // only when repeat allows it, because the statement may have committed.
    public async Task<TResult> ExecuteAsync<TResult>(
        Func<NpgsqlConnection, StatementAttempt, CancellationToken, Task<TResult>> statement,
        StatementRepeat repeat,
        CancellationToken cancellationToken)
    {
        var earlierAttemptMayHaveCommitted = false;
        for (var retry = 0; ; retry++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var canRetry = retry < _transientRetryDelays.Length;

            NpgsqlConnection connection;
            try
            {
                connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (canRetry && IsTransient(exception))
            {
                await Task.Delay(_transientRetryDelays[retry], cancellationToken).ConfigureAwait(false);
                continue;
            }

            await using (connection.ConfigureAwait(false))
            {
                try
                {
                    return await statement(
                            connection,
                            new StatementAttempt(earlierAttemptMayHaveCommitted),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    canRetry && repeat == StatementRepeat.SafeToRepeat && IsTransient(exception))
                {
                    earlierAttemptMayHaveCommitted = true;
                }
            }

            await Task.Delay(_transientRetryDelays[retry], cancellationToken).ConfigureAwait(false);
        }
    }

    public Task ExecuteAsync(
        Func<NpgsqlConnection, CancellationToken, Task> statement,
        StatementRepeat repeat,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            async (connection, _, token) =>
            {
                await statement(connection, token).ConfigureAwait(false);
                return true;
            },
            repeat,
            cancellationToken);

    // A command timeout is excluded: the statement is still running on the server and a retry
    // would queue behind it.
    internal static bool IsTransient(Exception exception)
        => exception is NpgsqlException { IsTransient: true } and not { InnerException: TimeoutException };
}

internal enum StatementRepeat
{
    // Sending the statement twice could change the outcome; its failure reaches the caller.
    NotAfterSend,

    // Sending the statement again after it may have committed leaves the same state.
    SafeToRepeat
}

// EarlierAttemptMayHaveCommitted:
// True when a previous attempt of this statement failed after it was sent, so "no row affected"
// can mean that attempt already applied the change.
internal readonly record struct StatementAttempt(bool EarlierAttemptMayHaveCommitted);

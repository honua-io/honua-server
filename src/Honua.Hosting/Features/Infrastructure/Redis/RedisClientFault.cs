// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Redis;

/// <summary>
/// Classifies the socket-pipe fault StackExchange.Redis raises when a connection is torn down
/// under a command that is still reading. The client completes the pipe reader in
/// <c>Shutdown</c> and then <c>ReadFromPipe</c> calls <c>AdvanceTo</c>, which throws
/// <see cref="InvalidOperationException"/> with <see cref="CompletedReaderMessage"/> instead of
/// a <c>RedisException</c> (honua-server#6753). Callers must match that message exactly:
/// other <see cref="InvalidOperationException"/> values are application errors.
/// </summary>
internal static class RedisClientFault
{
    /// <summary>Message raised by <c>Pipe.AdvanceReader</c> after the reader was completed.</summary>
    internal const string CompletedReaderMessage = "Reading is not allowed after reader was completed.";

    /// <summary>
    /// Whether <paramref name="exception"/> is the completed-reader pipe fault.
    /// </summary>
    internal static bool IsCompletedReader(Exception exception)
        => exception is InvalidOperationException
           && string.Equals(exception.Message, CompletedReaderMessage, StringComparison.Ordinal);

    /// <summary>
    /// Whether <paramref name="exception"/> or any exception it wraps is the completed-reader
    /// pipe fault. Stores translate Redis failures into their own typed exceptions, so the
    /// fault can arrive as an inner exception.
    /// </summary>
    internal static bool ContainsCompletedReader(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (IsCompletedReader(current))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="exception"/> is a Redis transport failure, including a timeout
    /// and the completed-reader pipe fault. Cancellation is not a transport failure.
    /// </summary>
    internal static bool IsRedisTransportFault(Exception exception)
        => exception is not OperationCanceledException
           && (exception is StackExchange.Redis.RedisException or TimeoutException || IsCompletedReader(exception));

    /// <summary>
    /// Whether an admin API-key store read failed because its backing store is unavailable.
    /// Includes database outages on the PostgreSQL registry used when Redis is absent.
    /// </summary>
    internal static bool IsCredentialStoreUnavailable(Exception exception)
        => IsRedisTransportFault(exception)
           || (exception is System.Data.Common.DbException && exception is not OperationCanceledException);

    /// <summary>
    /// Runs <paramref name="operation"/> once more when it throws <see cref="IsCompletedReader"/>.
    /// A Lambda freeze or a timeout can complete the pipe under one command; the multiplexer
    /// reconnects (<c>AbortOnConnectFail</c> is false) and the next command succeeds. A second
    /// fault propagates so the caller can fail closed.
    /// </summary>
    internal static async Task<T> RetryCompletedReaderAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsCompletedReader(ex) && !cancellationToken.IsCancellationRequested)
        {
            return await operation().ConfigureAwait(false);
        }
    }
}

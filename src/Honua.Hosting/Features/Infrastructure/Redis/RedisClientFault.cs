// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Redis;

// Callers must match the completed-reader message exactly; any other invalid-operation value is an application error.
internal static class RedisClientFault
{
    internal const string CompletedReaderMessage = "Reading is not allowed after reader was completed.";

    internal static bool IsCompletedReader(Exception exception)
        => exception is InvalidOperationException
           && string.Equals(exception.Message, CompletedReaderMessage, StringComparison.Ordinal);

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

    // Cancellation is not a transport failure.
    internal static bool IsRedisTransportFault(Exception exception)
        => exception is not OperationCanceledException
           && (exception is StackExchange.Redis.RedisException or TimeoutException || IsCompletedReader(exception));

    internal static bool IsCredentialStoreUnavailable(Exception exception)
        => IsRedisTransportFault(exception)
           || (exception is System.Data.Common.DbException && exception is not OperationCanceledException);

    // A completed-reader fault is retried once because a freeze can tear the pipe under one command; a second fault propagates so the caller fails closed.
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

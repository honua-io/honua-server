// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Checks h3-pg extension availability by querying pg_extension.
/// The result is cached after the first successful check.
/// </summary>
internal sealed partial class PostgresH3CapabilityChecker : IH3CapabilityChecker
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<PostgresH3CapabilityChecker> _logger;

    // Static cache so the result survives across scoped instances.
    // Note: assumes a single-database deployment. Multi-tenant setups with
    // different databases would need a cache keyed by connection string.
    // Values: 0=unknown, 1=available, 2=unavailable.
    // Transient failures leave this at 0 and set s_failureCacheExpiryMs instead.
    private static int s_cachedResult;
    private static long s_failureCacheExpiryMs;
    private static readonly SemaphoreSlim s_lock = new(1, 1);

    /// <summary>
    /// How long to cache a transient failure before re-probing. Prevents repeated
    /// serialized DB round-trips when the database is unreachable.
    /// Units: milliseconds (matching <see cref="Environment.TickCount64"/>).
    /// </summary>
    private static readonly long FailureCacheDurationMs = (long)TimeSpan.FromSeconds(60).TotalMilliseconds;

    public PostgresH3CapabilityChecker(
        IDatabaseConnectionProvider connectionProvider,
        ILogger<PostgresH3CapabilityChecker> logger)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool?> IsH3AvailableAsync(CancellationToken cancellationToken = default)
    {
        // Volatile reads ensure visibility of writes from other threads on
        // weakly-ordered architectures (ARM). On x86 these compile to plain loads.
        var cached = Volatile.Read(ref s_cachedResult);
        if (cached == 1) return true;
        if (cached == 2) return false;

        // Fast-path: if a transient failure was cached recently, return null
        // without re-acquiring the semaphore.
        var expiryMs = Volatile.Read(ref s_failureCacheExpiryMs);
        if (expiryMs > 0 && Environment.TickCount64 < expiryMs)
        {
            return null;
        }

        await s_lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring lock
            var cachedInLock = Volatile.Read(ref s_cachedResult);
            if (cachedInLock == 1) return true;
            if (cachedInLock == 2) return false;

            // Double-check transient failure cache inside lock
            if (s_failureCacheExpiryMs > 0 && Environment.TickCount64 < s_failureCacheExpiryMs)
            {
                return null;
            }

            await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                "SELECT EXISTS(SELECT 1 FROM pg_extension WHERE extname = 'h3')", connection);
            command.CommandTimeout = 10;

            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            var available = result is true;
            Volatile.Write(ref s_cachedResult, available ? 1 : 2);

            if (available)
            {
                LogH3ExtensionAvailable(_logger);
            }
            else
            {
                LogH3ExtensionMissing(_logger);
            }

            return available;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Cache the failure for a bounded period so that subsequent requests
            // fast-fail instead of serializing behind the semaphore while the
            // database is unreachable.
            Volatile.Write(ref s_failureCacheExpiryMs, Environment.TickCount64 + FailureCacheDurationMs);
            LogH3ExtensionCheckFailed(_logger, ex);
            return null;
        }
        finally
        {
            s_lock.Release();
        }
    }

    [LoggerMessage(
        EventId = 7900,
        Level = LogLevel.Information,
        Message = "h3-pg extension is available. H3 spatial indexing operations are enabled.")]
    private static partial void LogH3ExtensionAvailable(ILogger logger);

    [LoggerMessage(
        EventId = 7901,
        Level = LogLevel.Warning,
        Message = "h3-pg extension is not installed. H3 spatial indexing operations will return capability errors. Install h3-pg to enable H3 support: https://github.com/zachasme/h3-pg")]
    private static partial void LogH3ExtensionMissing(ILogger logger);

    [LoggerMessage(
        EventId = 7902,
        Level = LogLevel.Error,
        Message = "Failed to check h3-pg extension availability.")]
    private static partial void LogH3ExtensionCheckFailed(ILogger logger, Exception ex);
}

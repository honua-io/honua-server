// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Services;
using StackExchange.Redis;

namespace Honua.Infrastructure.Coordination;

/// <summary>
/// Redis-backed <see cref="IVersionLock"/> (#1553). Serializes branch-version reconcile/post/resolve
/// per (service, version) across every server replica using the StackExchange.Redis lock primitive
/// (<c>LockTakeAsync</c>/<c>LockExtendAsync</c>/<c>LockReleaseAsync</c>) — the same primitive the event
/// lease coordinator and execution-job store already use, so this reuses the established lock mechanism
/// rather than inventing a new one. When Redis is not connected it delegates to the single-node
/// in-process lock, which is correct for single-node and development/test deployments.
/// </summary>
internal sealed partial class RedisVersionLock : IVersionLock
{
    // The held lease is renewed at this fraction of its duration so a long reconcile/post never lets the
    // lock expire underneath it, while a crashed holder's lock still expires within the lease window.
    private static readonly TimeSpan RenewalFraction = TimeSpan.FromSeconds(30);
    private const string KeyPrefix = "honua:version:lock:";

    private readonly IDatabase? _database;
    private readonly InProcessVersionLock _fallback = new();
    private readonly ILogger<RedisVersionLock> _logger;

    public RedisVersionLock(IConnectionMultiplexer? redis, ILogger<RedisVersionLock> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _database = redis?.IsConnected == true ? redis.GetDatabase() : null;
    }

    /// <inheritdoc />
    public async Task<IVersionLockHandle?> TryAcquireAsync(
        string service,
        Guid versionId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);

        if (_database is null)
        {
            return await _fallback.TryAcquireAsync(service, versionId, leaseDuration, cancellationToken)
                .ConfigureAwait(false);
        }

        var key = $"{KeyPrefix}{service}:{versionId:N}";
        var token = Guid.NewGuid().ToString("N");

        bool acquired;
        try
        {
            acquired = await _database.LockTakeAsync(key, token, leaseDuration).ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            // A Redis outage must not silently drop mutual exclusion; degrade to the single-node lock so
            // at least same-node reconcile/post stay serialized, and log the degradation.
            Log.VersionLockRedisError(_logger, key, ex);
            return await _fallback.TryAcquireAsync(service, versionId, leaseDuration, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!acquired)
        {
            return null;
        }

        var renewalInterval = leaseDuration <= RenewalFraction
            ? TimeSpan.FromMilliseconds(Math.Max(leaseDuration.TotalMilliseconds / 2, 1))
            : RenewalFraction;
        return new RedisHandle(_database, key, token, leaseDuration, renewalInterval, _logger);
    }

    private sealed class RedisHandle : IVersionLockHandle
    {
        private readonly IDatabase _database;
        private readonly string _key;
        private readonly string _token;
        private readonly TimeSpan _leaseDuration;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _renewalLoop;
        private readonly ILogger _logger;
        private int _released;

        public RedisHandle(
            IDatabase database,
            string key,
            string token,
            TimeSpan leaseDuration,
            TimeSpan renewalInterval,
            ILogger logger)
        {
            _database = database;
            _key = key;
            _token = token;
            _leaseDuration = leaseDuration;
            _logger = logger;
            _renewalLoop = RenewAsync(renewalInterval, _cts.Token);
        }

        private async Task RenewAsync(TimeSpan interval, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                    await _database.LockExtendAsync(_key, _token, _leaseDuration).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on release.
            }
            catch (RedisException ex)
            {
                Log.VersionLockRedisError(_logger, _key, ex);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            await _cts.CancelAsync().ConfigureAwait(false);
            try
            {
                await _renewalLoop.ConfigureAwait(false);
            }
            catch
            {
                // Renewal-loop failures are already logged; release the lock regardless.
            }

            try
            {
                await _database.LockReleaseAsync(_key, _token).ConfigureAwait(false);
            }
            catch (RedisException)
            {
                // Best-effort release; the lease will expire if the release cannot reach Redis.
            }
            finally
            {
                _cts.Dispose();
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 7110,
            Level = LogLevel.Warning,
            Message = "Version lock Redis operation failed for {LockKey}; degrading to in-process lock.")]
        public static partial void VersionLockRedisError(ILogger logger, string lockKey, Exception exception);
    }
}

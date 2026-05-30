// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Honua.Infrastructure.Events;

/// <summary>
/// Background dispatcher that delivers persisted feature-change events to configured webhook subscribers.
/// </summary>
internal sealed partial class FeatureChangeWebhookDispatcher(
    IFeatureChangeEventStore store,
    IDistributedCache? distributedCache,
    IConnectionMultiplexer? redis,
    IHttpClientFactory httpClientFactory,
    IOptions<FeatureChangeWebhookOptions> options,
    ILogger<FeatureChangeWebhookDispatcher> logger) : BackgroundService
{
    private const string DeliveredCursorKey = "featurechange:webhook:delivered-cursor";
    private const string DispatchLeaseKey = "featurechange:webhook:lease";
    private const string DeliveryStateKeyPrefix = "featurechange:webhook:delivery:";
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DispatchLeaseDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LeaseRenewalInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DeliveryClaimTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DeliveryCompletedTtl = TimeSpan.FromDays(7);
    private const string DeliveryStateCompleted = "completed";
    private readonly IFeatureChangeEventStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IDistributedCache? _distributedCache = distributedCache;
    private readonly IConnectionMultiplexer? _redis = redis;
    private readonly IDatabase? _redisDb = redis?.GetDatabase();
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    private readonly FeatureChangeWebhookOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<FeatureChangeWebhookDispatcher> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly RedisLeaseCoordinator _leaseCoordinator = new(redis, DispatchLeaseKey, DispatchLeaseDuration);
    private readonly IFeatureChangeEventStoreHealth? _storeHealth = store as IFeatureChangeEventStoreHealth;
    private readonly ConcurrentDictionary<string, byte> _completedDeliveries = new(StringComparer.Ordinal);
    private int _invalidConfigurationLogged;
    private int _eventStoreUnavailableLogged;
    private int _unsafeDistributedModeLogged;
    private int _unsafeWebhookUrlLogged;
    private long _deliveredCursor;
    private volatile bool _cursorSyncRequired;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.Url))
                    {
                        await Task.Delay(IdlePollInterval, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(_options.Secret))
                    {
                        LogWebhookConfigurationInvalidOnce();
                        await Task.Delay(IdlePollInterval, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    if (_storeHealth is { CanPersistEvents: false })
                    {
                        LogEventStoreUnavailableOnce();
                        await Task.Delay(IdlePollInterval, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    if (_distributedCache != null &&
                        !_leaseCoordinator.IsConfigured)
                    {
                        LogUnsafeDistributedModeOnce();
                        await Task.Delay(IdlePollInterval, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    if (_leaseCoordinator.IsConfigured &&
                        !await _leaseCoordinator.TryAcquireOrExtendAsync().ConfigureAwait(false))
                    {
                        await Task.Delay(IdlePollInterval, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    if (!await EnsureDeliveredCursorReadyAsync(stoppingToken).ConfigureAwait(false))
                    {
                        await Task.Delay(IdlePollInterval, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    using var leaseRenewalCts = _leaseCoordinator.IsConfigured
                        ? CancellationTokenSource.CreateLinkedTokenSource(stoppingToken)
                        : null;
                    using var processingCts = _leaseCoordinator.IsConfigured
                        ? CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _leaseCoordinator.LeaseLostToken)
                        : null;
                    var leaseRenewalTask = leaseRenewalCts is null
                        ? Task.CompletedTask
                        : _leaseCoordinator.MaintainLeaseAsync(LeaseRenewalInterval, leaseRenewalCts.Token);
                    var processingToken = processingCts?.Token ?? stoppingToken;

                    try
                    {
                        var validation = await FeatureChangeWebhookUrlValidation
                            .ValidateAsync(_options.Url, processingToken)
                            .ConfigureAwait(false);
                        if (!validation.IsValid || validation.Uri == null)
                        {
                            LogWebhookUrlRejectedOnce(validation.ErrorMessage ?? FeatureChangeWebhookUrlValidation.InvalidHttpsUrlMessage);
                            await Task.Delay(IdlePollInterval, stoppingToken).ConfigureAwait(false);
                            continue;
                        }

                        var pending = await _store.QueryAsync(_deliveredCursor, null, null, limit: 100, processingToken).ConfigureAwait(false);
                        if (pending.Count == 0)
                        {
                            await Task.Delay(IdlePollInterval, stoppingToken).ConfigureAwait(false);
                            continue;
                        }

                        foreach (var featureEvent in pending)
                        {
                            processingToken.ThrowIfCancellationRequested();

                            if (_leaseCoordinator.IsConfigured &&
                                !await _leaseCoordinator.TryAcquireOrExtendAsync().ConfigureAwait(false))
                            {
                                break;
                            }

                            var deliveryClaim = await TryAcquireDeliveryClaimAsync(featureEvent.EventId).ConfigureAwait(false);
                            if (deliveryClaim.Result == DeliveryClaimResult.InProgress)
                            {
                                break;
                            }

                            if (deliveryClaim.Result == DeliveryClaimResult.Completed)
                            {
                                _deliveredCursor = featureEvent.Cursor;
                                if (!await TryPersistDeliveredCursorAsync(_deliveredCursor, processingToken).ConfigureAwait(false))
                                {
                                    break;
                                }

                                continue;
                            }

                            processingToken.ThrowIfCancellationRequested();

                            using var deliveryClaimRenewalCts = deliveryClaim.LeaseCoordinator is null
                                ? null
                                : CancellationTokenSource.CreateLinkedTokenSource(processingToken);
                            using var deliveryProcessingCts = deliveryClaim.LeaseCoordinator is null
                                ? null
                                : CancellationTokenSource.CreateLinkedTokenSource(
                                    processingToken,
                                    deliveryClaim.LeaseCoordinator.LeaseLostToken);
                            var deliveryClaimRenewalTask = deliveryClaimRenewalCts is null
                                ? Task.CompletedTask
                                : deliveryClaim.LeaseCoordinator!.MaintainLeaseAsync(LeaseRenewalInterval, deliveryClaimRenewalCts.Token);
                            var deliveryToken = deliveryProcessingCts?.Token ?? processingToken;

                            try
                            {
                                var delivered = await DeliverWithRetryAsync(featureEvent, validation.Uri, deliveryToken).ConfigureAwait(false);
                                if (!delivered)
                                {
                                    break;
                                }

                                await MarkDeliveryCompletedAsync(featureEvent.EventId).ConfigureAwait(false);
                                _deliveredCursor = featureEvent.Cursor;
                                if (!await TryPersistDeliveredCursorAsync(_deliveredCursor, processingToken).ConfigureAwait(false))
                                {
                                    break;
                                }
                            }
                            catch (OperationCanceledException) when (deliveryClaim.LeaseCoordinator?.LeaseLostToken.IsCancellationRequested == true)
                            {
                                break;
                            }
                            finally
                            {
                                if (deliveryClaimRenewalCts is not null)
                                {
                                    deliveryClaimRenewalCts.Cancel();
                                    await deliveryClaimRenewalTask.ConfigureAwait(false);
                                }

                                deliveryClaim.LeaseCoordinator?.Dispose();
                            }
                        }
                    }
                    finally
                    {
                        if (leaseRenewalCts is not null)
                        {
                            leaseRenewalCts.Cancel();
                            await leaseRenewalTask.ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException) when (_leaseCoordinator.IsConfigured && _leaseCoordinator.LeaseLostToken.IsCancellationRequested)
                {
                    await Task.Delay(IdlePollInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogDispatcherLoopFailed(_logger, ex);
                    await Task.Delay(IdlePollInterval, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await _leaseCoordinator.ReleaseAsync().ConfigureAwait(false);
        }
    }

    public override void Dispose()
    {
        _leaseCoordinator.Dispose();
        base.Dispose();
    }

    private async Task<bool> EnsureDeliveredCursorReadyAsync(CancellationToken cancellationToken)
    {
        if (_redisDb == null &&
            _distributedCache == null)
        {
            return true;
        }

        return _cursorSyncRequired
            ? await TrySyncDeliveredCursorAsync(cancellationToken).ConfigureAwait(false)
            : await TryLoadDeliveredCursorAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryLoadDeliveredCursorAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshDeliveredCursorAsync(cancellationToken).ConfigureAwait(false);
            _cursorSyncRequired = false;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogCursorLoadFailed(_logger, ex);
            return false;
        }
    }

    private async Task<bool> TryPersistDeliveredCursorAsync(long cursor, CancellationToken cancellationToken)
    {
        try
        {
            await PersistDeliveredCursorAsync(cursor, cancellationToken).ConfigureAwait(false);
            _cursorSyncRequired = false;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _deliveredCursor, cursor);
            _cursorSyncRequired = true;
            LogCursorPersistFailed(_logger, ex);
            return false;
        }
    }

    private async Task<bool> TrySyncDeliveredCursorAsync(CancellationToken cancellationToken)
    {
        return await TryPersistDeliveredCursorAsync(
                Volatile.Read(ref _deliveredCursor),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RefreshDeliveredCursorAsync(CancellationToken cancellationToken)
    {
        var loadedCursor = await LoadDeliveredCursorAsync(cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var currentCursor = Volatile.Read(ref _deliveredCursor);
            if (loadedCursor <= currentCursor)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _deliveredCursor, loadedCursor, currentCursor) == currentCursor)
            {
                return;
            }
        }
    }

    private async Task<bool> DeliverWithRetryAsync(FeatureChangeEvent featureEvent, Uri webhookUri, CancellationToken cancellationToken)
    {
        var destinationValidation = await FeatureChangeWebhookUrlValidation
            .ValidateAsync(webhookUri.AbsoluteUri, cancellationToken)
            .ConfigureAwait(false);
        if (!destinationValidation.IsValid || destinationValidation.Uri == null)
        {
            LogWebhookUrlRejectedOnce(destinationValidation.ErrorMessage ?? FeatureChangeWebhookUrlValidation.InvalidHttpsUrlMessage);
            return false;
        }

        var payload = JsonSerializer.Serialize(featureEvent, FeatureChangeEventsJsonContext.Default.FeatureChangeEvent);
        return await WebhookDeliveryHelper.DeliverWithRetryAsync(
            new WebhookDeliveryRequest
            {
                Payload = payload,
                EventId = featureEvent.EventId,
                Timestamp = featureEvent.Timestamp,
                WebhookUri = destinationValidation.Uri,
                Secret = _options.Secret!,
                HttpClientName = "feature-change-webhook",
                MaxAttempts = _options.MaxAttempts,
                InitialBackoffMs = _options.InitialBackoffMs,
                MaxBackoffMs = _options.MaxBackoffMs,
                RequestTimeoutSeconds = _options.RequestTimeoutSeconds
            },
            _httpClientFactory,
            _logger,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<DeliveryClaimState> TryAcquireDeliveryClaimAsync(string eventId)
    {
        if (_redisDb != null)
        {
            var claimLease = new RedisLeaseCoordinator(_redis, GetDeliveryStateKey(eventId), DeliveryClaimTtl);
            if (await claimLease.TryAcquireOrExtendAsync().ConfigureAwait(false))
            {
                return new DeliveryClaimState(DeliveryClaimResult.Acquired, claimLease);
            }

            claimLease.Dispose();
            var stateKey = GetDeliveryStateKey(eventId);
            var existingState = await _redisDb.StringGetAsync(stateKey).ConfigureAwait(false);
            return string.Equals(existingState.ToString(), DeliveryStateCompleted, StringComparison.Ordinal)
                ? new DeliveryClaimState(DeliveryClaimResult.Completed, null)
                : new DeliveryClaimState(DeliveryClaimResult.InProgress, null);
        }

        return _completedDeliveries.ContainsKey(eventId)
            ? new DeliveryClaimState(DeliveryClaimResult.Completed, null)
            : new DeliveryClaimState(DeliveryClaimResult.Acquired, null);
    }

    private async Task MarkDeliveryCompletedAsync(string eventId)
    {
        if (_redisDb != null)
        {
            await _redisDb.StringSetAsync(
                    GetDeliveryStateKey(eventId),
                    DeliveryStateCompleted,
                    DeliveryCompletedTtl)
                .ConfigureAwait(false);
            return;
        }

        _completedDeliveries[eventId] = 0;
    }

    private async Task<long> LoadDeliveredCursorAsync(CancellationToken cancellationToken)
    {
        if (_redisDb != null)
        {
            var redisValue = await _redisDb.StringGetAsync(DeliveredCursorKey).ConfigureAwait(false);
            return long.TryParse(redisValue.ToString(), out var parsedRedisCursor) ? parsedRedisCursor : 0L;
        }

        if (_distributedCache == null)
        {
            return Volatile.Read(ref _deliveredCursor);
        }

        var cacheValue = await _distributedCache.GetStringAsync(DeliveredCursorKey, cancellationToken).ConfigureAwait(false);
        return long.TryParse(cacheValue, out var parsedCacheCursor) ? parsedCacheCursor : 0L;
    }

    private async Task PersistDeliveredCursorAsync(long cursor, CancellationToken cancellationToken)
    {
        if (_redisDb != null)
        {
            await _redisDb.StringSetAsync(
                    DeliveredCursorKey,
                    cursor.ToString(CultureInfo.InvariantCulture),
                    TimeSpan.FromDays(7))
                .ConfigureAwait(false);
            return;
        }

        if (_distributedCache == null)
        {
            Volatile.Write(ref _deliveredCursor, cursor);
            return;
        }

        await _distributedCache.SetStringAsync(
                DeliveredCursorKey,
                cursor.ToString(CultureInfo.InvariantCulture),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(7) },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string GetDeliveryStateKey(string eventId) => $"{DeliveryStateKeyPrefix}{eventId}";

    private enum DeliveryClaimResult
    {
        Acquired,
        InProgress,
        Completed
    }

    private readonly record struct DeliveryClaimState(
        DeliveryClaimResult Result,
        RedisLeaseCoordinator? LeaseCoordinator);

    private void LogWebhookConfigurationInvalidOnce()
    {
        if (Interlocked.Exchange(ref _invalidConfigurationLogged, 1) == 0)
        {
            LogWebhookConfigurationInvalid(_logger);
        }
    }

    private void LogEventStoreUnavailableOnce()
    {
        if (Interlocked.Exchange(ref _eventStoreUnavailableLogged, 1) == 0)
        {
            LogEventStoreUnavailable(_logger);
        }
    }

    private void LogWebhookUrlRejectedOnce(string reason)
    {
        if (Interlocked.Exchange(ref _unsafeWebhookUrlLogged, 1) == 0)
        {
            LogWebhookUrlRejected(_logger, reason);
        }
    }

    private void LogUnsafeDistributedModeOnce()
    {
        if (Interlocked.Exchange(ref _unsafeDistributedModeLogged, 1) == 0)
        {
            LogUnsafeDistributedMode(_logger);
        }
    }

    [LoggerMessage(EventId = 9101, Level = LogLevel.Warning, Message = "Feature change webhook is enabled but secret is missing; delivery is disabled.")]
    private static partial void LogWebhookConfigurationInvalid(ILogger logger);

    [LoggerMessage(EventId = 9110, Level = LogLevel.Warning, Message = "Feature change webhook delivery is disabled because durable event storage is unavailable in this environment.")]
    private static partial void LogEventStoreUnavailable(ILogger logger);

    [LoggerMessage(EventId = 9106, Level = LogLevel.Warning, Message = "Feature change webhook cursor load failed; continuing with in-memory cursor state.")]
    private static partial void LogCursorLoadFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9107, Level = LogLevel.Warning, Message = "Feature change webhook cursor persistence failed; continuing with in-memory cursor state.")]
    private static partial void LogCursorPersistFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9108, Level = LogLevel.Warning, Message = "Feature change webhook dispatch loop failed; retrying.")]
    private static partial void LogDispatcherLoopFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9105, Level = LogLevel.Warning, Message = "Feature change webhook delivery is disabled because the configured URL is unsafe: {Reason}")]
    private static partial void LogWebhookUrlRejected(ILogger logger, string reason);

    [LoggerMessage(EventId = 9109, Level = LogLevel.Warning, Message = "Feature change webhook delivery requires Redis lease coordination when distributed cache is enabled; delivery is disabled in fallback mode.")]
    private static partial void LogUnsafeDistributedMode(ILogger logger);
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Events;

/// <summary>
/// Background dispatcher that delivers persisted feature-change events to configured webhook subscribers.
/// </summary>
internal sealed partial class FeatureChangeWebhookDispatcher(
    IFeatureChangeEventStore store,
    IDistributedCache? distributedCache,
    IHttpClientFactory httpClientFactory,
    IOptions<FeatureChangeWebhookOptions> options,
    ILogger<FeatureChangeWebhookDispatcher> logger) : BackgroundService
{
    private const string DeliveredCursorKey = "featurechange:webhook:delivered-cursor";
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(1);
    private readonly IFeatureChangeEventStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IDistributedCache? _distributedCache = distributedCache;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    private readonly FeatureChangeWebhookOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<FeatureChangeWebhookDispatcher> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private int _invalidConfigurationLogged;
    private int _unsafeWebhookUrlLogged;
    private long _deliveredCursor;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _deliveredCursor = await TryLoadDeliveredCursorAsync(stoppingToken).ConfigureAwait(false);

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

                var validation = await FeatureChangeWebhookUrlValidation
                    .ValidateAsync(_options.Url, stoppingToken)
                    .ConfigureAwait(false);
                if (!validation.IsValid || validation.Uri == null)
                {
                    LogWebhookUrlRejectedOnce(validation.ErrorMessage ?? FeatureChangeWebhookUrlValidation.InvalidHttpsUrlMessage);
                    await Task.Delay(IdlePollInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var pending = await _store.QueryAsync(_deliveredCursor, null, null, limit: 100, stoppingToken).ConfigureAwait(false);
                if (pending.Count == 0)
                {
                    await Task.Delay(IdlePollInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                foreach (var featureEvent in pending)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    var delivered = await DeliverWithRetryAsync(featureEvent, validation.Uri, stoppingToken).ConfigureAwait(false);
                    if (!delivered)
                    {
                        break;
                    }

                    _deliveredCursor = featureEvent.Cursor;
                    await TryPersistDeliveredCursorAsync(_deliveredCursor, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogDispatcherLoopFailed(_logger, ex);
                await Task.Delay(IdlePollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<long> TryLoadDeliveredCursorAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await LoadDeliveredCursorAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogCursorLoadFailed(_logger, ex);
            return Volatile.Read(ref _deliveredCursor);
        }
    }

    private async Task TryPersistDeliveredCursorAsync(long cursor, CancellationToken cancellationToken)
    {
        try
        {
            await PersistDeliveredCursorAsync(cursor, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _deliveredCursor, cursor);
            LogCursorPersistFailed(_logger, ex);
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

    private async Task<long> LoadDeliveredCursorAsync(CancellationToken cancellationToken)
    {
        if (_distributedCache == null)
        {
            return Volatile.Read(ref _deliveredCursor);
        }

        var value = await _distributedCache.GetStringAsync(DeliveredCursorKey, cancellationToken).ConfigureAwait(false);
        return long.TryParse(value, out var cursor) ? cursor : 0L;
    }

    private async Task PersistDeliveredCursorAsync(long cursor, CancellationToken cancellationToken)
    {
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

    private void LogWebhookConfigurationInvalidOnce()
    {
        if (Interlocked.Exchange(ref _invalidConfigurationLogged, 1) == 0)
        {
            LogWebhookConfigurationInvalid(_logger);
        }
    }

    private void LogWebhookUrlRejectedOnce(string reason)
    {
        if (Interlocked.Exchange(ref _unsafeWebhookUrlLogged, 1) == 0)
        {
            LogWebhookUrlRejected(_logger, reason);
        }
    }

    [LoggerMessage(EventId = 9101, Level = LogLevel.Warning, Message = "Feature change webhook is enabled but secret is missing; delivery is disabled.")]
    private static partial void LogWebhookConfigurationInvalid(ILogger logger);

    [LoggerMessage(EventId = 9106, Level = LogLevel.Warning, Message = "Feature change webhook cursor load failed; continuing with in-memory cursor state.")]
    private static partial void LogCursorLoadFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9107, Level = LogLevel.Warning, Message = "Feature change webhook cursor persistence failed; continuing with in-memory cursor state.")]
    private static partial void LogCursorPersistFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9108, Level = LogLevel.Warning, Message = "Feature change webhook dispatch loop failed; retrying.")]
    private static partial void LogDispatcherLoopFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9105, Level = LogLevel.Warning, Message = "Feature change webhook delivery is disabled because the configured URL is unsafe: {Reason}")]
    private static partial void LogWebhookUrlRejected(ILogger logger, string reason);
}

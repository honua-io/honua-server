// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Alerts;

internal sealed partial class AlertDispatchBackgroundService : BackgroundService, IAlertDispatchHealth
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Dictionary<AlertChannelType, IAlertDeliverySink> _sinks;
    private readonly AlertOptions _options;
    private readonly AlertNotificationRateLimiter _rateLimiter;
    private readonly ILogger<AlertDispatchBackgroundService> _logger;

    private volatile bool _isRunning;
    private long _lastPollAtTicks;
    private volatile bool _storagePollFailing;
    private AlertDispatchBacklog? _lastBacklog;

    public AlertDispatchBackgroundService(
        IServiceScopeFactory scopeFactory,
        IEnumerable<IAlertDeliverySink> sinks,
        AlertNotificationRateLimiter rateLimiter,
        IOptions<AlertOptions> options,
        ILogger<AlertDispatchBackgroundService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _sinks = (sinks ?? throw new ArgumentNullException(nameof(sinks))).ToDictionary(static sink => sink.ChannelType);
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool IsDispatcherRunning => _isRunning;

    /// <inheritdoc />
    public bool IsDispatcherEnabled => _options.Enabled;

    /// <inheritdoc />
    public DateTimeOffset? LastPollAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastPollAtTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <inheritdoc />
    public AlertDispatchBacklog? LastBacklog => Volatile.Read(ref _lastBacklog);

    /// <inheritdoc />
    public bool IsStoragePollFailing => _storagePollFailing;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            LogDisabled(_logger);
            return;
        }

        _isRunning = true;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var dispatchStore = scope.ServiceProvider.GetRequiredService<IAlertDispatchStore>();
                    var eventStore = scope.ServiceProvider.GetRequiredService<IAlertEventStore>();

                    var batch = await dispatchStore
                        .ClaimPendingAsync(_options.Dispatch.ClaimBatchSize, now, stoppingToken)
                        .ConfigureAwait(false);

                    foreach (var item in batch)
                    {
                        await ProcessItemAsync(dispatchStore, eventStore, item, stoppingToken).ConfigureAwait(false);
                    }

                    await RefreshBacklogAsync(dispatchStore, stoppingToken).ConfigureAwait(false);
                    Interlocked.Exchange(ref _lastPollAtTicks, now.UtcTicks);

                    if (batch.Count == 0)
                    {
                        await Task.Delay(_options.Dispatch.IdleDelay, stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _storagePollFailing = true;
                    LogLoopFailed(_logger, ex);
                    await Task.Delay(_options.Dispatch.IdleDelay, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _isRunning = false;
        }
    }

    private async Task RefreshBacklogAsync(IAlertDispatchStore dispatchStore, CancellationToken cancellationToken)
    {
        var backlog = await dispatchStore.GetBacklogAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _lastBacklog, backlog);
        _storagePollFailing = false;
        AlertPipelineMetrics.RecordBacklog(backlog.PendingCount, backlog.DeadLetteredCount);
    }

    private async Task ProcessItemAsync(
        IAlertDispatchStore dispatchStore,
        IAlertEventStore eventStore,
        AlertDispatchItem item,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        if (!_sinks.TryGetValue(item.ChannelType, out var sink))
        {
            await dispatchStore
                .MarkFailedAsync(item.DispatchId, now, now, deadLetter: true, "No delivery sink registered.", cancellationToken)
                .ConfigureAwait(false);
            AlertPipelineMetrics.RecordDeliveryFailed(item.ChannelType, deadLettered: true, latencyMs: 0);
            return;
        }

        var alertEvent = await eventStore.GetAsync(item.EventId, cancellationToken).ConfigureAwait(false);
        if (alertEvent is null)
        {
            await dispatchStore
                .MarkFailedAsync(item.DispatchId, now, now, deadLetter: true, "Alert event not found.", cancellationToken)
                .ConfigureAwait(false);
            AlertPipelineMetrics.RecordDeliveryFailed(item.ChannelType, deadLettered: true, latencyMs: 0);
            return;
        }

        // Per-channel notification rate cap: enforced BEFORE the sink call. A capped
        // dispatch is rescheduled (retry budget untouched, never dead-lettered) so a
        // burst is smoothed rather than dropped.
        if (!_rateLimiter.TryAcquire(item.ChannelType, _options.Dispatch.MaxNotificationsPerMinutePerChannel, now))
        {
            var deferUntil = AlertDispatchRetryPolicy.ComputeNextAttempt(1, now, _options.Dispatch);
            await dispatchStore.RescheduleAsync(item.DispatchId, deferUntil, cancellationToken).ConfigureAwait(false);
            AlertPipelineMetrics.RecordDeliveryRateCapped(item.ChannelType);
            LogRateCapped(_logger, item.DispatchId, item.ChannelType, _options.Dispatch.MaxNotificationsPerMinutePerChannel);
            return;
        }

        var startTimestamp = AlertPipelineMetrics.StartTimestamp();
        var result = await sink.DeliverAsync(item, alertEvent, cancellationToken).ConfigureAwait(false);
        var elapsedMs = AlertPipelineMetrics.ElapsedMilliseconds(startTimestamp);

        if (result.Succeeded)
        {
            await dispatchStore.MarkDeliveredAsync(item.DispatchId, now, cancellationToken).ConfigureAwait(false);
            AlertPipelineMetrics.RecordDeliverySucceeded(item.ChannelType, elapsedMs);
            LogDelivered(_logger, item.DispatchId, item.ChannelType);
            return;
        }

        var nextAttempt = AlertDispatchRetryPolicy.ComputeNextAttempt(item.Attempts + 1, now, _options.Dispatch);
        var exhausted = item.Attempts + 1 >= item.MaxAttempts || !result.Retryable;

        await dispatchStore
            .MarkFailedAsync(item.DispatchId, now, nextAttempt, exhausted, result.Error, cancellationToken)
            .ConfigureAwait(false);

        AlertPipelineMetrics.RecordDeliveryFailed(item.ChannelType, exhausted, elapsedMs);
        LogFailed(_logger, item.DispatchId, item.ChannelType, exhausted, result.Error ?? "Delivery failed.");
    }

    [LoggerMessage(EventId = 9420, Level = LogLevel.Information, Message = "Alert dispatcher is disabled.")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(EventId = 9421, Level = LogLevel.Debug, Message = "Alert dispatch delivered for dispatch {DispatchId} using {ChannelType}.")]
    private static partial void LogDelivered(ILogger logger, long dispatchId, AlertChannelType channelType);

    [LoggerMessage(EventId = 9422, Level = LogLevel.Warning, Message = "Alert dispatch failed for dispatch {DispatchId} using {ChannelType}. Exhausted={Exhausted}. Error={Error}.")]
    private static partial void LogFailed(ILogger logger, long dispatchId, AlertChannelType channelType, bool exhausted, string error);

    [LoggerMessage(EventId = 9423, Level = LogLevel.Warning, Message = "Alert dispatch loop failed.")]
    private static partial void LogLoopFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9424, Level = LogLevel.Information, Message = "Alert dispatch {DispatchId} for {ChannelType} deferred by the per-channel notification rate cap ({MaxPerMinute}/min).")]
    private static partial void LogRateCapped(ILogger logger, long dispatchId, AlertChannelType channelType, int maxPerMinute);
}

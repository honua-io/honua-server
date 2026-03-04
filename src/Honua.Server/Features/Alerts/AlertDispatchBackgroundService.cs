// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Alerts;

internal sealed partial class AlertDispatchBackgroundService : BackgroundService
{
    private readonly IAlertDispatchStore _dispatchStore;
    private readonly IAlertEventStore _eventStore;
    private readonly Dictionary<AlertChannelType, IAlertDeliverySink> _sinks;
    private readonly AlertOptions _options;
    private readonly ILogger<AlertDispatchBackgroundService> _logger;

    public AlertDispatchBackgroundService(
        IAlertDispatchStore dispatchStore,
        IAlertEventStore eventStore,
        IEnumerable<IAlertDeliverySink> sinks,
        IOptions<AlertOptions> options,
        ILogger<AlertDispatchBackgroundService> logger)
    {
        _dispatchStore = dispatchStore ?? throw new ArgumentNullException(nameof(dispatchStore));
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _sinks = (sinks ?? throw new ArgumentNullException(nameof(sinks))).ToDictionary(static sink => sink.ChannelType);
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            LogDisabled(_logger);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var batch = await _dispatchStore
                    .ClaimPendingAsync(_options.Dispatch.ClaimBatchSize, now, stoppingToken)
                    .ConfigureAwait(false);

                if (batch.Count == 0)
                {
                    await Task.Delay(_options.Dispatch.IdleDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                foreach (var item in batch)
                {
                    await ProcessItemAsync(item, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogLoopFailed(_logger, ex);
                await Task.Delay(_options.Dispatch.IdleDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessItemAsync(AlertDispatchItem item, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        if (!_sinks.TryGetValue(item.ChannelType, out var sink))
        {
            await _dispatchStore
                .MarkFailedAsync(item.DispatchId, now, now, deadLetter: true, "No delivery sink registered.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var alertEvent = await _eventStore.GetAsync(item.EventId, cancellationToken).ConfigureAwait(false);
        if (alertEvent is null)
        {
            await _dispatchStore
                .MarkFailedAsync(item.DispatchId, now, now, deadLetter: true, "Alert event not found.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var result = await sink.DeliverAsync(item, alertEvent, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            await _dispatchStore.MarkDeliveredAsync(item.DispatchId, now, cancellationToken).ConfigureAwait(false);
            LogDelivered(_logger, item.DispatchId, item.ChannelType);
            return;
        }

        var nextAttempt = ComputeNextAttempt(item.Attempts + 1, now);
        var exhausted = item.Attempts + 1 >= item.MaxAttempts || !result.Retryable;

        await _dispatchStore
            .MarkFailedAsync(item.DispatchId, now, nextAttempt, exhausted, result.Error, cancellationToken)
            .ConfigureAwait(false);

        LogFailed(_logger, item.DispatchId, item.ChannelType, exhausted, result.Error ?? "Delivery failed.");
    }

    private DateTimeOffset ComputeNextAttempt(int attemptNumber, DateTimeOffset now)
    {
        var exponent = Math.Clamp(attemptNumber - 1, 0, 10);
        var multiplier = 1 << exponent;

        var rawDelay = TimeSpan.FromTicks(_options.Dispatch.InitialBackoff.Ticks * multiplier);
        var delay = rawDelay > _options.Dispatch.MaxBackoff
            ? _options.Dispatch.MaxBackoff
            : rawDelay;

        return now + delay;
    }

    [LoggerMessage(EventId = 9420, Level = LogLevel.Information, Message = "Alert dispatcher is disabled.")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(EventId = 9421, Level = LogLevel.Debug, Message = "Alert dispatch delivered for dispatch {DispatchId} using {ChannelType}.")]
    private static partial void LogDelivered(ILogger logger, long dispatchId, AlertChannelType channelType);

    [LoggerMessage(EventId = 9422, Level = LogLevel.Warning, Message = "Alert dispatch failed for dispatch {DispatchId} using {ChannelType}. Exhausted={Exhausted}. Error={Error}.")]
    private static partial void LogFailed(ILogger logger, long dispatchId, AlertChannelType channelType, bool exhausted, string error);

    [LoggerMessage(EventId = 9423, Level = LogLevel.Warning, Message = "Alert dispatch loop failed.")]
    private static partial void LogLoopFailed(ILogger logger, Exception exception);
}

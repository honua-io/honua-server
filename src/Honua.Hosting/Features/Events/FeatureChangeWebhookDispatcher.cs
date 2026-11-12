// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Infrastructure.Coordination;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Events;

// One task across the cluster delivers: the lease holder. Without a lease store the process is the
// only deliverer. Delivery is at least once: the cursor is written after the webhook accepts an event.
internal sealed partial class FeatureChangeWebhookDispatcher(
    IFeatureChangeEventStore store,
    IStreamCursorStore cursors,
    ILeaseStore? leases,
    IHttpClientFactory httpClientFactory,
    IOptions<FeatureChangeWebhookOptions> options,
    ILogger<FeatureChangeWebhookDispatcher> logger) : BackgroundService
{
    internal const string StreamName = "feature-change-webhook";
    private const int BatchSize = 100;
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DispatchLeaseDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LeaseRenewalInterval = TimeSpan.FromSeconds(1);
    private readonly IFeatureChangeEventStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IStreamCursorStore _cursors = cursors ?? throw new ArgumentNullException(nameof(cursors));
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    private readonly FeatureChangeWebhookOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<FeatureChangeWebhookDispatcher> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly LeaseHolder? _lease = leases is null ? null : new LeaseHolder(leases, StreamName, DispatchLeaseDuration);
    private int _invalidConfigurationLogged;
    private int _unsafeWebhookUrlLogged;

    // Null until read under the lease; cleared when the lease is lost, because another holder may have moved it.
    private long? _deliveredCursor;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!await RunPassAsync(stoppingToken).ConfigureAwait(false))
                    {
                        await Task.Delay(IdlePollInterval, stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException) when (_lease is { HasLease: false })
                {
                    await Task.Delay(IdlePollInterval, stoppingToken).ConfigureAwait(false);
                }
                // A single failed pass must not stop the host; the next poll retries it.
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    LogDispatcherLoopFailed(_logger, ex);
                    await Task.Delay(IdlePollInterval, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (_lease is not null)
            {
                await _lease.ReleaseAsync().ConfigureAwait(false);
            }
        }
    }

    public override void Dispose()
    {
        _lease?.Dispose();
        base.Dispose();
    }

    // Returns true when a full pass delivered events, so more may be waiting.
    private async Task<bool> RunPassAsync(CancellationToken stoppingToken)
    {
        if (!IsDeliveryConfigured() || !await TryHoldLeaseAsync(stoppingToken).ConfigureAwait(false))
        {
            return false;
        }

        using var renewalCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var processingCts = _lease is null
            ? CancellationTokenSource.CreateLinkedTokenSource(stoppingToken)
            : CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _lease.LeaseLostToken);
        var renewalTask = _lease?.MaintainAsync(LeaseRenewalInterval, renewalCts.Token) ?? Task.CompletedTask;
        try
        {
            return await DeliverPendingAsync(processingCts.Token).ConfigureAwait(false);
        }
        finally
        {
            await renewalCts.CancelAsync().ConfigureAwait(false);
            await renewalTask.ConfigureAwait(false);
        }
    }

    private bool IsDeliveryConfigured()
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.Url))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_options.Secret))
        {
            LogWebhookConfigurationInvalidOnce();
            return false;
        }

        return true;
    }

    private async Task<bool> TryHoldLeaseAsync(CancellationToken cancellationToken)
    {
        if (_lease is null)
        {
            return true;
        }

        var heldBefore = _lease.HasLease;
        if (!await _lease.TryAcquireOrExtendAsync(cancellationToken).ConfigureAwait(false))
        {
            _deliveredCursor = null;
            return false;
        }

        if (!heldBefore)
        {
            _deliveredCursor = null;
        }

        return true;
    }

    private async Task<bool> DeliverPendingAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Url))
        {
            return false;
        }

        var validation = await FeatureChangeWebhookUrlValidation
            .ValidateAsync(_options.Url, cancellationToken)
            .ConfigureAwait(false);
        if (!validation.IsValid || validation.Uri == null)
        {
            LogWebhookUrlRejectedOnce(validation.ErrorMessage ?? FeatureChangeWebhookUrlValidation.InvalidHttpsUrlMessage);
            return false;
        }

        var cursor = await TryLoadDeliveredCursorAsync(cancellationToken).ConfigureAwait(false);
        if (cursor is null)
        {
            return false;
        }

        var pending = await _store.QueryAsync(cursor, null, null, BatchSize, cancellationToken).ConfigureAwait(false);
        foreach (var featureEvent in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await DeliverWithRetryAsync(featureEvent, validation.Uri, cancellationToken).ConfigureAwait(false) ||
                !await TryAdvanceCursorAsync(featureEvent.Cursor, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        return pending.Count > 0;
    }

    // A first start begins at the head, so enabling the webhook never replays history.
    private async Task<long?> TryLoadDeliveredCursorAsync(CancellationToken cancellationToken)
    {
        if (_deliveredCursor is { } loaded)
        {
            return loaded;
        }

        try
        {
            var stored = await _cursors.ReadAsync(StreamName, cancellationToken).ConfigureAwait(false);
            if (stored is null)
            {
                stored = await _store.GetCurrentCursorAsync(cancellationToken).ConfigureAwait(false);
                await _cursors.WriteAsync(StreamName, stored.Value, cancellationToken).ConfigureAwait(false);
            }

            _deliveredCursor = stored;
            return stored;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            LogCursorLoadFailed(_logger, ex);
            return null;
        }
    }

    // The in-memory cursor advances even when the write fails, so this process does not resend the
    // event; the next successful write covers it.
    private async Task<bool> TryAdvanceCursorAsync(long position, CancellationToken cancellationToken)
    {
        _deliveredCursor = position;
        try
        {
            await _cursors.WriteAsync(StreamName, position, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            LogCursorPersistFailed(_logger, ex);
            return false;
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

    [LoggerMessage(EventId = 9106, Level = LogLevel.Warning, Message = "Feature change webhook cursor load failed; retrying.")]
    private static partial void LogCursorLoadFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9107, Level = LogLevel.Warning, Message = "Feature change webhook cursor persistence failed; retrying.")]
    private static partial void LogCursorPersistFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9108, Level = LogLevel.Warning, Message = "Feature change webhook dispatch loop failed; retrying.")]
    private static partial void LogDispatcherLoopFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9105, Level = LogLevel.Warning, Message = "Feature change webhook delivery is disabled because the configured URL is unsafe: {Reason}")]
    private static partial void LogWebhookUrlRejected(ILogger logger, string reason);
}

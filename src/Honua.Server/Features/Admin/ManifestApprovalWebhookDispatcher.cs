// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Events;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Background service that delivers manifest approval webhook events to configured endpoints.
/// Follows the same signed-payload, retry, and backoff patterns as <see cref="FeatureChangeWebhookDispatcher"/>.
/// </summary>
internal sealed partial class ManifestApprovalWebhookDispatcher : BackgroundService
{
    private static readonly TimeSpan DeliveryRetryDelay = TimeSpan.FromSeconds(1);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ManifestApprovalWebhookOptions _options;
    private readonly ILogger<ManifestApprovalWebhookDispatcher> _logger;
    private readonly System.Threading.Channels.Channel<ManifestApprovalWebhookEvent> _channel;
    private int _invalidConfigurationLogged;
    private int _unsafeWebhookUrlLogged;

    public ManifestApprovalWebhookDispatcher(
        IHttpClientFactory httpClientFactory,
        IOptions<ManifestApprovalWebhookOptions> options,
        ILogger<ManifestApprovalWebhookDispatcher> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _channel = System.Threading.Channels.Channel.CreateBounded<ManifestApprovalWebhookEvent>(
            new System.Threading.Channels.BoundedChannelOptions(1000)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest
            });
    }

    /// <summary>
    /// Enqueues an approval webhook event for delivery.
    /// </summary>
    public bool Enqueue(ManifestApprovalWebhookEvent webhookEvent)
    {
        return _channel.Writer.TryWrite(webhookEvent);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var webhookEvent in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var delivered = await TryDeliverAsync(webhookEvent, stoppingToken).ConfigureAwait(false);
                    if (delivered)
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogDispatcherFailed(_logger, webhookEvent.EventId, ex);
                }

                await Task.Delay(DeliveryRetryDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> TryDeliverAsync(ManifestApprovalWebhookEvent webhookEvent, CancellationToken stoppingToken)
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

        var validation = await FeatureChangeWebhookUrlValidation
            .ValidateAsync(_options.Url, stoppingToken)
            .ConfigureAwait(false);
        if (!validation.IsValid || validation.Uri == null)
        {
            LogWebhookUrlRejectedOnce(validation.ErrorMessage ?? FeatureChangeWebhookUrlValidation.InvalidHttpsUrlMessage);
            return false;
        }

        var payload = JsonSerializer.Serialize(webhookEvent, ManifestApprovalJsonContext.Default.ManifestApprovalWebhookEvent);
        return await WebhookDeliveryHelper.DeliverWithRetryAsync(
            new WebhookDeliveryRequest
            {
                Payload = payload,
                EventId = webhookEvent.EventId,
                Timestamp = webhookEvent.Timestamp,
                WebhookUri = validation.Uri,
                Secret = _options.Secret!,
                HttpClientName = "manifest-approval-webhook",
                MaxAttempts = _options.MaxAttempts,
                InitialBackoffMs = _options.InitialBackoffMs,
                MaxBackoffMs = _options.MaxBackoffMs,
                RequestTimeoutSeconds = _options.RequestTimeoutSeconds
            },
            _httpClientFactory,
            _logger,
            stoppingToken).ConfigureAwait(false);
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

    [LoggerMessage(EventId = 9210, Level = LogLevel.Warning, Message = "Manifest approval webhook is enabled but secret is missing; delivery is disabled.")]
    private static partial void LogWebhookConfigurationInvalid(ILogger logger);

    [LoggerMessage(EventId = 9211, Level = LogLevel.Warning, Message = "Manifest approval webhook delivery is disabled because the configured URL is unsafe: {Reason}")]
    private static partial void LogWebhookUrlRejected(ILogger logger, string reason);

    [LoggerMessage(EventId = 9212, Level = LogLevel.Warning, Message = "Manifest approval webhook dispatch failed for event {EventId}.")]
    private static partial void LogDispatcherFailed(ILogger logger, string eventId, Exception exception);
}

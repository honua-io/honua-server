// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Events;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Background service that detects manifest drift and delivers webhook notifications.
/// </summary>
internal sealed partial class ManifestDriftWebhookDispatcher(
    IServiceScopeFactory scopeFactory,
    IDistributedCache? distributedCache,
    IHttpClientFactory httpClientFactory,
    IOptions<ManifestDriftWebhookOptions> options,
    ILogger<ManifestDriftWebhookDispatcher> logger) : BackgroundService
{
    private const string LastDriftHashKey = "manifest:drift:last-hash";
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    private readonly IDistributedCache? _distributedCache = distributedCache;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    private readonly ManifestDriftWebhookOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<ManifestDriftWebhookDispatcher> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private int _invalidConfigurationLogged;
    private int _unsafeWebhookUrlLogged;
    private string? _lastDriftHash;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _lastDriftHash = await TryLoadLastDriftHashAsync(stoppingToken).ConfigureAwait(false);
        var pollInterval = TimeSpan.FromSeconds(Math.Max(10, _options.PollIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.Url))
                {
                    await Task.Delay(pollInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(_options.Secret))
                {
                    LogConfigurationInvalidOnce();
                    await Task.Delay(pollInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var validation = await FeatureChangeWebhookUrlValidation
                    .ValidateAsync(_options.Url, stoppingToken)
                    .ConfigureAwait(false);
                if (!validation.IsValid || validation.Uri == null)
                {
                    LogWebhookUrlRejectedOnce(validation.ErrorMessage ?? FeatureChangeWebhookUrlValidation.InvalidHttpsUrlMessage);
                    await Task.Delay(pollInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await CheckAndDispatchDriftAsync(validation.Uri, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogDispatcherLoopFailed(_logger, ex);
            }

            await Task.Delay(pollInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task CheckAndDispatchDriftAsync(Uri webhookUri, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var resourceStore = scope.ServiceProvider.GetRequiredService<IMetadataResourceStore>();
        var versionStore = scope.ServiceProvider.GetRequiredService<IManifestVersionStore>();

        // Get latest manifest version
        var baseline = await versionStore.GetLatestAsync(cancellationToken).ConfigureAwait(false);
        if (baseline == null)
        {
            return;
        }

        // Get actual resources
        var actualResources = await resourceStore.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        // Compute drift
        var driftRecords = ManifestHashHelper.ComputeDrift(baseline.ManifestJson, actualResources);
        if (driftRecords.Count == 0)
        {
            // No drift — clear stored hash so next drift is detected
            if (_lastDriftHash != null)
            {
                _lastDriftHash = null;
                await TryPersistLastDriftHashAsync(null, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        // Compute a hash of the drift state to detect changes
        var driftHash = ComputeDriftHash(driftRecords);
        if (string.Equals(_lastDriftHash, driftHash, StringComparison.Ordinal))
        {
            return; // Same drift state, don't spam
        }

        var report = new ManifestDriftReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            BaselineVersionId = baseline.VersionId,
            HasDrift = true,
            Resources = driftRecords
        };

        var delivered = await DeliverWithRetryAsync(report, webhookUri, cancellationToken).ConfigureAwait(false);
        if (delivered)
        {
            _lastDriftHash = driftHash;
            await TryPersistLastDriftHashAsync(driftHash, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string ComputeDriftHash(List<ManifestDriftRecord> records)
    {
        var builder = new StringBuilder();
        foreach (var record in records
                     .OrderBy(r => r.Identifier.Kind, StringComparer.Ordinal)
                     .ThenBy(r => r.Identifier.Namespace, StringComparer.Ordinal)
                     .ThenBy(r => r.Identifier.Name, StringComparer.Ordinal))
        {
            builder.Append(record.Identifier.Kind).Append('|')
                .Append(record.Identifier.Namespace).Append('|')
                .Append(record.Identifier.Name).Append('|')
                .Append(record.DriftType).Append('|')
                .Append(record.DeclaredHash).Append('|')
                .Append(record.ActualHash).Append('\n');
        }

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private async Task<bool> DeliverWithRetryAsync(ManifestDriftReport report, Uri webhookUri, CancellationToken cancellationToken)
    {
        var destinationValidation = await FeatureChangeWebhookUrlValidation
            .ValidateAsync(webhookUri.AbsoluteUri, cancellationToken)
            .ConfigureAwait(false);
        if (!destinationValidation.IsValid || destinationValidation.Uri == null)
        {
            LogWebhookUrlRejectedOnce(destinationValidation.ErrorMessage ?? FeatureChangeWebhookUrlValidation.InvalidHttpsUrlMessage);
            return false;
        }

        webhookUri = destinationValidation.Uri;
        var payload = JsonSerializer.Serialize(report, MetadataResourceJsonContext.Default.ManifestDriftReport);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = ComputeSignature(_options.Secret!, timestamp, payload);
        var maxAttempts = Math.Max(1, _options.MaxAttempts);
        var eventId = Guid.NewGuid().ToString("N");

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.RequestTimeoutSeconds)));

                using var request = new HttpRequestMessage(HttpMethod.Post, webhookUri)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                Honua.Server.Features.Infrastructure.Events.WebhookDeliveryHelper.AddValidatedHeader(request.Headers, "X-Honua-Event-Id", eventId);
                Honua.Server.Features.Infrastructure.Events.WebhookDeliveryHelper.AddValidatedHeader(request.Headers, "X-Honua-Event-Type", "manifest.drift");
                Honua.Server.Features.Infrastructure.Events.WebhookDeliveryHelper.AddValidatedHeader(request.Headers, "X-Honua-Event-Timestamp", timestamp);
                Honua.Server.Features.Infrastructure.Events.WebhookDeliveryHelper.AddValidatedHeader(request.Headers, "X-Honua-Signature", $"sha256={signature}");
                Honua.Server.Features.Infrastructure.Events.WebhookDeliveryHelper.AddValidatedHeader(request.Headers, "Idempotency-Key", eventId);

                var client = _httpClientFactory.CreateClient("manifest-drift-webhook");
                using var response = await client.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    LogDeliverySucceeded(_logger, eventId);
                    return true;
                }

                var isRetryable = (int)response.StatusCode >= 500 || response.StatusCode == System.Net.HttpStatusCode.TooManyRequests;
                LogDeliveryFailed(_logger, eventId, attempt, (int)response.StatusCode);
                if (!isRetryable || attempt == maxAttempts)
                {
                    return false;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogDeliveryException(_logger, eventId, attempt, ex);
                if (attempt == maxAttempts)
                {
                    return false;
                }
            }

            var delayMs = Math.Min(
                Math.Max(1, _options.InitialBackoffMs) * (1 << (attempt - 1)),
                Math.Max(1, _options.MaxBackoffMs));
            await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static string ComputeSignature(string secret, string timestamp, string payload)
        => WebhookSignatureHelper.ComputeSignature(secret, timestamp, payload);

    private async Task<string?> TryLoadLastDriftHashAsync(CancellationToken cancellationToken)
    {
        if (_distributedCache == null)
        {
            return null;
        }

        try
        {
            return await _distributedCache.GetStringAsync(LastDriftHashKey, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task TryPersistLastDriftHashAsync(string? hash, CancellationToken cancellationToken)
    {
        if (_distributedCache == null)
        {
            return;
        }

        try
        {
            if (hash == null)
            {
                await _distributedCache.RemoveAsync(LastDriftHashKey, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _distributedCache.SetStringAsync(
                        LastDriftHashKey,
                        hash,
                        new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(7) },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Best-effort persistence
        }
    }

    private void LogConfigurationInvalidOnce()
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

    [LoggerMessage(EventId = 9201, Level = LogLevel.Warning, Message = "Manifest drift webhook is enabled but secret is missing; delivery is disabled.")]
    private static partial void LogWebhookConfigurationInvalid(ILogger logger);

    [LoggerMessage(EventId = 9206, Level = LogLevel.Warning, Message = "Manifest drift webhook delivery is disabled because the configured URL is unsafe: {Reason}")]
    private static partial void LogWebhookUrlRejected(ILogger logger, string reason);

    [LoggerMessage(EventId = 9202, Level = LogLevel.Debug, Message = "Manifest drift webhook delivery succeeded for event {EventId}.")]
    private static partial void LogDeliverySucceeded(ILogger logger, string eventId);

    [LoggerMessage(EventId = 9203, Level = LogLevel.Warning, Message = "Manifest drift webhook delivery failed for event {EventId} on attempt {Attempt} with status {StatusCode}.")]
    private static partial void LogDeliveryFailed(ILogger logger, string eventId, int attempt, int statusCode);

    [LoggerMessage(EventId = 9204, Level = LogLevel.Warning, Message = "Manifest drift webhook delivery threw for event {EventId} on attempt {Attempt}.")]
    private static partial void LogDeliveryException(ILogger logger, string eventId, int attempt, Exception exception);

    [LoggerMessage(EventId = 9205, Level = LogLevel.Warning, Message = "Manifest drift webhook dispatch loop failed; retrying.")]
    private static partial void LogDispatcherLoopFailed(ILogger logger, Exception exception);
}

/// <summary>
/// Configuration options for the manifest drift webhook dispatcher.
/// </summary>
public sealed class ManifestDriftWebhookOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "ManifestDrift:Webhook";

    /// <summary>
    /// Enables outbound drift webhook delivery.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Absolute webhook URL for drift notifications.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Shared HMAC secret for signature generation.
    /// </summary>
    public string? Secret { get; set; }

    /// <summary>
    /// Drift polling interval in seconds.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum delivery attempts per event.
    /// </summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// Base retry delay in milliseconds.
    /// </summary>
    public int InitialBackoffMs { get; set; } = 500;

    /// <summary>
    /// Upper bound for retry delay in milliseconds.
    /// </summary>
    public int MaxBackoffMs { get; set; } = 30_000;

    /// <summary>
    /// Per-request timeout for webhook calls in seconds.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 15;
}

/// <summary>
/// Startup validator for <see cref="ManifestDriftWebhookOptions"/>.
/// </summary>
internal sealed class ManifestDriftWebhookOptionsValidator : IValidateOptions<ManifestDriftWebhookOptions>
{
    public ValidateOptionsResult Validate(string? name, ManifestDriftWebhookOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Url))
        {
            failures.Add("Manifest drift webhook URL must be configured when webhook delivery is enabled.");
        }
        else
        {
            var validation = FeatureChangeWebhookUrlValidation.ValidateConfiguration(options.Url);
            if (!validation.IsValid)
            {
                failures.Add(validation.ErrorMessage ?? FeatureChangeWebhookUrlValidation.InvalidHttpsUrlMessage);
            }
        }

        if (string.IsNullOrWhiteSpace(options.Secret))
        {
            failures.Add("Manifest drift webhook secret must be configured when webhook delivery is enabled.");
        }

        if (options.MaxAttempts < 1)
        {
            failures.Add("Manifest drift webhook MaxAttempts must be at least 1.");
        }

        if (options.RequestTimeoutSeconds < 1)
        {
            failures.Add("Manifest drift webhook RequestTimeoutSeconds must be at least 1 second.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

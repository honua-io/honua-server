// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Monitoring;

/// <summary>
/// Configuration options for intelligent alerting system.
/// </summary>
public sealed class IntelligentAlertingOptions
{
    /// <summary>
    /// Whether to enable smart alerting with noise reduction.
    /// </summary>
    public bool EnableSmartAlerting { get; set; } = true;

    /// <summary>
    /// The minimum time between similar alerts in minutes.
    /// </summary>
    public int AlertSuppressionMinutes { get; set; } = 15;

    /// <summary>
    /// The maximum number of alerts to send per hour.
    /// </summary>
    public int MaxAlertsPerHour { get; set; } = 20;

    /// <summary>
    /// Whether to enable predictive alerting.
    /// </summary>
    public bool EnablePredictiveAlerting { get; set; } = true;

    /// <summary>
    /// The threshold for predictive alert confidence.
    /// </summary>
    public double PredictiveAlertThreshold { get; set; } = 0.7;

    /// <summary>
    /// Notification channels configuration.
    /// </summary>
    public NotificationChannelOptions NotificationChannels { get; set; } = new();

    /// <summary>
    /// Alert escalation rules.
    /// </summary>
    public EscalationRuleOptions[] EscalationRules { get; set; } = Array.Empty<EscalationRuleOptions>();
}

/// <summary>
/// Configuration for notification channels.
/// </summary>
public sealed class NotificationChannelOptions
{
    public EmailChannelOptions? Email { get; set; }
    public SlackChannelOptions? Slack { get; set; }
    public WebhookChannelOptions? Webhook { get; set; }
    public SmsChannelOptions? Sms { get; set; }
}

public sealed class EmailChannelOptions
{
    public bool Enabled { get; set; }
    public string SmtpServer { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string[] Recipients { get; set; } = Array.Empty<string>();
}

public sealed class SlackChannelOptions
{
    public bool Enabled { get; set; }
    public string WebhookUrl { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
}

public sealed class WebhookChannelOptions
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = string.Empty;
    public Dictionary<string, string> Headers { get; set; } = new();
}

public sealed class SmsChannelOptions
{
    public bool Enabled { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public string[] PhoneNumbers { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Configuration for alert escalation rules.
/// </summary>
public sealed class EscalationRuleOptions
{
    public string[] AlertTypes { get; set; } = Array.Empty<string>();
    public string[] Severities { get; set; } = Array.Empty<string>();
    public int EscalationTimeMinutes { get; set; } = 30;
    public string[] EscalationChannels { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Service for intelligent alerting with context-aware notifications and noise reduction.
/// Provides smart alert routing, escalation management, and predictive notifications.
/// </summary>
public interface IIntelligentAlertingService
{
    /// <summary>
    /// Sends an alert through the intelligent alerting system.
    /// </summary>
    /// <param name="alert">The alert to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendAlertAsync(IntelligentAlert alert, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a predictive alert for potential issues.
    /// </summary>
    /// <param name="prediction">The predictive alert information.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendPredictiveAlertAsync(PredictiveAlert prediction, CancellationToken cancellationToken = default);

    /// <summary>
    /// Acknowledges an alert to stop escalation.
    /// </summary>
    /// <param name="alertId">The ID of the alert to acknowledge.</param>
    /// <param name="acknowledgedBy">Who acknowledged the alert.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AcknowledgeAlertAsync(string alertId, string acknowledgedBy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current alert statistics.
    /// </summary>
    /// <returns>Alert statistics summary.</returns>
    Task<AlertStatistics> GetAlertStatisticsAsync();

    /// <summary>
    /// Gets active alerts that haven't been resolved.
    /// </summary>
    /// <returns>Collection of active alerts.</returns>
    Task<IEnumerable<ActiveAlert>> GetActiveAlertsAsync();
}

/// <summary>
/// Implementation of intelligent alerting service with smart routing and escalation.
/// </summary>
internal sealed class IntelligentAlertingService : IIntelligentAlertingService, IHostedService, IDisposable
{
    private static readonly Action<ILogger, string, Exception?> LogAlertSuppressed =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(1001, "AlertSuppressed"), "Alert suppressed due to smart alerting rules: {AlertType}");

    private static readonly Action<ILogger, string, Exception?> LogAlertRateLimited =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1002, "AlertRateLimited"), "Alert rate limit exceeded, queuing alert: {AlertType}");

    private static readonly Action<ILogger, string, string, Exception?> LogAlertQueued =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(1003, "AlertQueued"), "Alert queued for delivery: {AlertId} - {AlertType}");

    private static readonly Action<ILogger, string, string, Exception?> LogAlertAcknowledged =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(1004, "AlertAcknowledged"), "Alert acknowledged: {AlertId} by {User}");

    private static readonly Action<ILogger, string, Exception?> LogNotificationError =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(1005, "NotificationError"), "Error processing notification {NotificationId}");

    private static readonly Action<ILogger, string, string, Exception?> LogChannelFailure =
        LoggerMessage.Define<string, string>(LogLevel.Error, new EventId(1006, "ChannelFailure"), "Failed to send notification to {Channel} for alert {AlertId}");

    private static readonly Action<ILogger, string, string, string, Exception?> LogNotificationSent =
        LoggerMessage.Define<string, string, string>(LogLevel.Information, new EventId(1007, "NotificationSent"), "Sending alert {AlertId} to {Channel}: {Title}");

    private static readonly Action<ILogger, string, Exception?> LogEscalationError =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(1008, "EscalationError"), "Error escalating alert {AlertId}");

    private static readonly Action<ILogger, string, int, Exception?> LogAlertEscalated =
        LoggerMessage.Define<string, int>(LogLevel.Warning, new EventId(1009, "AlertEscalated"), "Escalating alert {AlertId} to level {Level}");

    private readonly IntelligentAlertingOptions _options;
    // Anomaly detection service removed - unused in current implementation
    private readonly ILogger<IntelligentAlertingService> _logger;
    private readonly ConcurrentDictionary<string, AlertHistory> _alertHistory = new();
    private readonly ConcurrentDictionary<string, ActiveAlert> _activeAlerts = new();
    private readonly ConcurrentQueue<NotificationRequest> _notificationQueue = new();
    private readonly Timer _processingTimer;
    private readonly Timer _escalationTimer;

    public IntelligentAlertingService(
        IOptions<IntelligentAlertingOptions> options,
        ILogger<IntelligentAlertingService> logger)
    {
        _options = options.Value;
        _logger = logger;

        _processingTimer = new Timer(ProcessNotificationQueue, null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        _escalationTimer = new Timer(CheckEscalations, null, TimeSpan.Zero, TimeSpan.FromMinutes(5));
    }

    public async Task SendAlertAsync(IntelligentAlert alert, CancellationToken cancellationToken = default)
    {
        // Apply smart alerting logic
        if (_options.EnableSmartAlerting)
        {
            var shouldSuppress = await ShouldSuppressAlertAsync(alert);
            if (shouldSuppress)
            {
                LogAlertSuppressed(_logger, alert.Type, null);
                return;
            }
        }

        // Check rate limiting
        if (await IsRateLimitedAsync())
        {
            LogAlertRateLimited(_logger, alert.Type, null);
            // In a production system, you might queue this for later or upgrade its priority
            return;
        }

        // Create active alert
        var activeAlert = new ActiveAlert
        {
            Id = Guid.NewGuid().ToString(),
            OriginalAlert = alert,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = AlertStatus.Active,
            EscalationLevel = 0
        };

        _activeAlerts.TryAdd(activeAlert.Id, activeAlert);

        // Queue notification
        var notification = new NotificationRequest
        {
            Id = activeAlert.Id,
            Alert = alert,
            Channels = DetermineNotificationChannels(alert),
            CreatedAt = DateTimeOffset.UtcNow
        };

        _notificationQueue.Enqueue(notification);

        // Record alert history
        RecordAlertHistory(alert);

        LogAlertQueued(_logger, activeAlert.Id, alert.Type, null);

        // Record telemetry
        using var activity = HonuaTelemetry.StartActivity("honua.alert.sent");
        activity?.SetTag("alert.type", alert.Type);
        activity?.SetTag("alert.severity", alert.Severity);
    }

    public async Task SendPredictiveAlertAsync(PredictiveAlert prediction, CancellationToken cancellationToken = default)
    {
        if (!_options.EnablePredictiveAlerting || prediction.Confidence < _options.PredictiveAlertThreshold)
        {
            return;
        }

        var alert = new IntelligentAlert
        {
            Type = "PredictiveAlert",
            Title = $"Predicted Issue: {prediction.PredictedIssue}",
            Description = $"System prediction indicates potential issue: {prediction.Description}",
            Severity = DeterminePredictiveSeverity(prediction.Confidence),
            Source = "PredictiveEngine",
            Metadata = new Dictionary<string, object>
            {
                { "confidence", prediction.Confidence },
                { "predicted_time", prediction.PredictedTime },
                { "issue_type", prediction.PredictedIssue }
            }
        };

        await SendAlertAsync(alert, cancellationToken);
    }

    public async Task AcknowledgeAlertAsync(string alertId, string acknowledgedBy, CancellationToken cancellationToken = default)
    {
        if (_activeAlerts.TryGetValue(alertId, out var activeAlert))
        {
            activeAlert.Status = AlertStatus.Acknowledged;
            activeAlert.AcknowledgedBy = acknowledgedBy;
            activeAlert.AcknowledgedAt = DateTimeOffset.UtcNow;

            LogAlertAcknowledged(_logger, alertId, acknowledgedBy, null);

            // Record telemetry
            using var activity = HonuaTelemetry.StartActivity("honua.alert.acknowledged");
            activity?.SetTag("alert.id", alertId);
            activity?.SetTag("acknowledged.by", acknowledgedBy);
        }
    }

    public async Task<AlertStatistics> GetAlertStatisticsAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var last24Hours = now.AddDays(-1);
        var lastWeek = now.AddDays(-7);

        var recentHistory = _alertHistory.Values
            .Where(h => h.LastAlertTime >= last24Hours)
            .ToArray();

        var weeklyHistory = _alertHistory.Values
            .Where(h => h.LastAlertTime >= lastWeek)
            .ToArray();

        return new AlertStatistics
        {
            ActiveAlerts = _activeAlerts.Count,
            AlertsLast24Hours = recentHistory.Sum(h => h.Count),
            AlertsLastWeek = weeklyHistory.Sum(h => h.Count),
            TopAlertTypes = recentHistory
                .GroupBy(h => h.AlertType)
                .OrderByDescending(g => g.Sum(h => h.Count))
                .Take(5)
                .ToDictionary(g => g.Key, g => g.Sum(h => h.Count)),
            AverageResolutionTimeMinutes = CalculateAverageResolutionTime(),
            EscalationRate = CalculateEscalationRate()
        };
    }

    public async Task<IEnumerable<ActiveAlert>> GetActiveAlertsAsync()
    {
        return _activeAlerts.Values.Where(a => a.Status == AlertStatus.Active || a.Status == AlertStatus.Acknowledged);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _processingTimer?.Dispose();
        _escalationTimer?.Dispose();
        return Task.CompletedTask;
    }

    private async Task<bool> ShouldSuppressAlertAsync(IntelligentAlert alert)
    {
        var alertKey = GetAlertKey(alert);
        if (_alertHistory.TryGetValue(alertKey, out var history))
        {
            var timeSinceLastAlert = DateTimeOffset.UtcNow - history.LastAlertTime;
            if (timeSinceLastAlert < TimeSpan.FromMinutes(_options.AlertSuppressionMinutes))
            {
                return true; // Suppress due to recent similar alert
            }
        }

        return false;
    }

    private async Task<bool> IsRateLimitedAsync()
    {
        var hourAgo = DateTimeOffset.UtcNow.AddHours(-1);
        var recentAlerts = _alertHistory.Values
            .Where(h => h.LastAlertTime >= hourAgo)
            .Sum(h => h.Count);

        return recentAlerts >= _options.MaxAlertsPerHour;
    }

    private string[] DetermineNotificationChannels(IntelligentAlert alert)
    {
        var channels = new List<string>();

        // Default channels based on severity
        switch (alert.Severity.ToLowerInvariant())
        {
            case "critical":
                if (_options.NotificationChannels.Sms?.Enabled == true)
                    channels.Add("sms");
                if (_options.NotificationChannels.Slack?.Enabled == true)
                    channels.Add("slack");
                if (_options.NotificationChannels.Email?.Enabled == true)
                    channels.Add("email");
                break;
            case "high":
                if (_options.NotificationChannels.Slack?.Enabled == true)
                    channels.Add("slack");
                if (_options.NotificationChannels.Email?.Enabled == true)
                    channels.Add("email");
                break;
            case "medium":
            case "low":
                if (_options.NotificationChannels.Email?.Enabled == true)
                    channels.Add("email");
                break;
        }

        if (_options.NotificationChannels.Webhook?.Enabled == true)
        {
            channels.Add("webhook");
        }

        return channels.ToArray();
    }

    private void RecordAlertHistory(IntelligentAlert alert)
    {
        var alertKey = GetAlertKey(alert);
        _alertHistory.AddOrUpdate(alertKey,
            new AlertHistory { AlertType = alert.Type, Count = 1, LastAlertTime = DateTimeOffset.UtcNow },
            (_, existing) => existing with { Count = existing.Count + 1, LastAlertTime = DateTimeOffset.UtcNow });
    }

    private string GetAlertKey(IntelligentAlert alert)
    {
        // Create a key that groups similar alerts together
        return $"{alert.Type}:{alert.Severity}:{alert.Source}";
    }

    private string DeterminePredictiveSeverity(double confidence)
    {
        return confidence switch
        {
            >= 0.9 => "High",
            >= 0.8 => "Medium",
            _ => "Low"
        };
    }

    private double CalculateAverageResolutionTime()
    {
        var resolvedAlerts = _activeAlerts.Values
            .Where(a => a.Status == AlertStatus.Resolved && a.ResolvedAt.HasValue)
            .ToArray();

        if (resolvedAlerts.Length == 0)
        {
            return 0;
        }

        var totalMinutes = resolvedAlerts
            .Sum(a => (a.ResolvedAt!.Value - a.CreatedAt).TotalMinutes);

        return totalMinutes / resolvedAlerts.Length;
    }

    private double CalculateEscalationRate()
    {
        var totalAlerts = _activeAlerts.Count;
        if (totalAlerts == 0)
        {
            return 0;
        }

        var escalatedAlerts = _activeAlerts.Values.Count(a => a.EscalationLevel > 0);
        return (double)escalatedAlerts / totalAlerts * 100;
    }

    private async void ProcessNotificationQueue(object? state)
    {
        while (_notificationQueue.TryDequeue(out var notification))
        {
            try
            {
                await ProcessNotificationAsync(notification);
            }
            catch (Exception ex)
            {
                LogNotificationError(_logger, notification.Id, ex);
            }
        }
    }

    private async Task ProcessNotificationAsync(NotificationRequest notification)
    {
        foreach (var channel in notification.Channels)
        {
            try
            {
                await SendNotificationToChannelAsync(notification, channel);
            }
            catch (Exception ex)
            {
                LogChannelFailure(_logger, channel, notification.Id, ex);
            }
        }
    }

    private async Task SendNotificationToChannelAsync(NotificationRequest notification, string channel)
    {
        // In a real implementation, this would integrate with actual notification services
        LogNotificationSent(_logger, notification.Id, channel, notification.Alert.Title, null);

        // Simulate notification delivery
        await Task.Delay(100);
    }

    private async void CheckEscalations(object? state)
    {
        var now = DateTimeOffset.UtcNow;
        var alertsToEscalate = _activeAlerts.Values
            .Where(a => a.Status == AlertStatus.Active)
            .Where(a => ShouldEscalate(a, now))
            .ToArray();

        foreach (var alert in alertsToEscalate)
        {
            try
            {
                await EscalateAlertAsync(alert);
            }
            catch (Exception ex)
            {
                LogEscalationError(_logger, alert.Id, ex);
            }
        }
    }

    private bool ShouldEscalate(ActiveAlert alert, DateTimeOffset now)
    {
        var escalationRule = _options.EscalationRules
            .FirstOrDefault(r => r.AlertTypes.Contains(alert.OriginalAlert.Type) &&
                                r.Severities.Contains(alert.OriginalAlert.Severity));

        if (escalationRule == null)
        {
            return false;
        }

        var timeSinceCreated = now - alert.CreatedAt;
        return timeSinceCreated >= TimeSpan.FromMinutes(escalationRule.EscalationTimeMinutes);
    }

    private async Task EscalateAlertAsync(ActiveAlert alert)
    {
        alert.EscalationLevel++;
        alert.LastEscalatedAt = DateTimeOffset.UtcNow;

        LogAlertEscalated(_logger, alert.Id, alert.EscalationLevel, null);

        // In a real implementation, this would send escalated notifications
        // to different channels or people based on escalation rules
    }

    public void Dispose()
    {
        _processingTimer?.Dispose();
        _escalationTimer?.Dispose();
    }
}

// Supporting models for the intelligent alerting system

public sealed record IntelligentAlert
{
    public string Type { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public Dictionary<string, object> Metadata { get; init; } = new();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record PredictiveAlert
{
    public string PredictedIssue { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public double Confidence { get; init; }
    public DateTimeOffset PredictedTime { get; init; }
    public Dictionary<string, object> Indicators { get; init; } = new();
}

public sealed record AlertHistory
{
    public string AlertType { get; init; } = string.Empty;
    public int Count { get; init; }
    public DateTimeOffset LastAlertTime { get; init; }
}

public enum AlertStatus
{
    Active,
    Acknowledged,
    Resolved,
    Suppressed
}

public sealed record ActiveAlert
{
    public string Id { get; init; } = string.Empty;
    public IntelligentAlert OriginalAlert { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; }
    public AlertStatus Status { get; set; }
    public int EscalationLevel { get; set; }
    public DateTimeOffset? LastEscalatedAt { get; set; }
    public string? AcknowledgedBy { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}

public sealed record NotificationRequest
{
    public string Id { get; init; } = string.Empty;
    public IntelligentAlert Alert { get; init; } = new();
    public string[] Channels { get; init; } = Array.Empty<string>();
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record AlertStatistics
{
    public int ActiveAlerts { get; init; }
    public int AlertsLast24Hours { get; init; }
    public int AlertsLastWeek { get; init; }
    public Dictionary<string, int> TopAlertTypes { get; init; } = new();
    public double AverageResolutionTimeMinutes { get; init; }
    public double EscalationRate { get; init; }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Alerts.Domain;

namespace Honua.Server.Tests.Features.Alerts;

/// <summary>
/// Shared test factory methods for alert delivery sink tests.
/// </summary>
internal static class AlertTestFixtures
{
    public static AlertDispatchItem CreateDispatchItem(
        AlertChannelType channelType,
        string? destination = null) => new()
        {
            DispatchId = 1,
            EventId = 2,
            ChannelType = channelType,
            Destination = destination,
            Status = AlertDispatchStatus.Pending,
            Attempts = 0,
            MaxAttempts = 3,
            NextAttemptAt = DateTimeOffset.UtcNow
        };

    public static AlertEventEnvelope CreateAlertEvent(
        string dedupeKey = "evt-test-1",
        AlertSeverity severity = AlertSeverity.Warning,
        AlertIncidentStatus incidentStatus = AlertIncidentStatus.Started) => new()
        {
            DedupeKey = dedupeKey,
            RuleId = 42,
            ServiceId = "svc-1",
            LayerId = 7,
            ObjectId = 100,
            TriggerType = AlertTriggerType.Enter,
            Generation = 1,
            Severity = severity,
            OccurredAt = DateTimeOffset.UtcNow,
            PayloadJson = "{\"test\":true}",
            IncidentStatus = incidentStatus
        };
}

/// <summary>
/// Test HTTP handler that returns a fixed status code.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;

    public FakeHttpMessageHandler(HttpStatusCode statusCode)
    {
        _statusCode = statusCode;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new HttpResponseMessage(_statusCode));
    }
}

internal sealed class CapturingHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;

    public CapturingHttpMessageHandler(HttpStatusCode statusCode)
    {
        _statusCode = statusCode;
    }

    public string? LastRequestBody { get; private set; }

    public Uri? LastRequestUri { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri;
        LastRequestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        return new HttpResponseMessage(_statusCode);
    }
}

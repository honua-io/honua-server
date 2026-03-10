// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Alerts.Domain;
using Honua.Server.Features.Alerts;

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

internal sealed class FakeSnsPublisher : ISnsPublisher
{
    public string? LastTopicArn { get; private set; }

    public Dictionary<string, string>? LastAttributes { get; private set; }

    public Func<string, string, string?, Dictionary<string, string>, CancellationToken, Task<SnsPublishResult>>? PublishAsyncHandler { get; init; }

    public Task<SnsPublishResult> PublishAsync(
        string topicArn,
        string message,
        string? subject,
        Dictionary<string, string> attributes,
        CancellationToken cancellationToken)
    {
        LastTopicArn = topicArn;
        LastAttributes = attributes;

        return PublishAsyncHandler is null
            ? Task.FromResult(new SnsPublishResult(true, false, null))
            : PublishAsyncHandler(topicArn, message, subject, attributes, cancellationToken);
    }
}

internal sealed class FakeSqsPublisher : ISqsPublisher
{
    public string? LastQueueUrl { get; private set; }

    public Dictionary<string, string>? LastAttributes { get; private set; }

    public Func<string, string, Dictionary<string, string>, CancellationToken, Task<SqsPublishResult>>? SendMessageAsyncHandler { get; init; }

    public Task<SqsPublishResult> SendMessageAsync(
        string queueUrl,
        string messageBody,
        Dictionary<string, string> attributes,
        CancellationToken cancellationToken)
    {
        LastQueueUrl = queueUrl;
        LastAttributes = attributes;

        return SendMessageAsyncHandler is null
            ? Task.FromResult(new SqsPublishResult(true, false, null))
            : SendMessageAsyncHandler(queueUrl, messageBody, attributes, cancellationToken);
    }
}

internal sealed class FakeEventHubPublisher : IEventHubPublisher
{
    public Dictionary<string, object>? LastProperties { get; private set; }

    public Func<BinaryData, Dictionary<string, object>, CancellationToken, Task<EventHubPublishResult>>? SendAsyncHandler { get; init; }

    public Task<EventHubPublishResult> SendAsync(
        BinaryData body,
        Dictionary<string, object> properties,
        CancellationToken cancellationToken)
    {
        LastProperties = properties;

        return SendAsyncHandler is null
            ? Task.FromResult(new EventHubPublishResult(true, false, null))
            : SendAsyncHandler(body, properties, cancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

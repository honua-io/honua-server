// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Alerts.Domain;
using Honua.Server.Features.Alerts;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class WebhookAlertDeliverySinkTests
{
    [UnitTest]
    public async Task DeliverAsync_WithUnsafeDestination_DoesNotSendRequest()
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var handler = new CountingHandler();
        httpClientFactory.CreateClient("alerts-webhook").Returns(new HttpClient(handler));

        var sink = new WebhookAlertDeliverySink(
            httpClientFactory,
            Options.Create(new AlertOptions()));

        var result = await sink.DeliverAsync(
            new AlertDispatchItem
            {
                DispatchId = 1,
                EventId = 2,
                ChannelType = AlertChannelType.Webhook,
                Destination = "https://localhost/webhook",
                Status = AlertDispatchStatus.Pending,
                Attempts = 0,
                MaxAttempts = 3,
                NextAttemptAt = DateTimeOffset.UtcNow
            },
            new AlertEventEnvelope
            {
                DedupeKey = "evt-1",
                RuleId = 99,
                ServiceId = "svc-1",
                LayerId = 7,
                ObjectId = 100,
                TriggerType = AlertTriggerType.Enter,
                Generation = 1,
                Severity = AlertSeverity.Warning,
                OccurredAt = DateTimeOffset.UtcNow,
                PayloadJson = "{}"
            });

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Contains("Webhook destination", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, handler.SendCount);
        httpClientFactory.DidNotReceive().CreateClient("alerts-webhook");
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}

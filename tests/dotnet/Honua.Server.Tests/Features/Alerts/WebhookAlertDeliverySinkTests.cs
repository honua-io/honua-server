// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Alerts.Domain;
using Honua.Alerts;
using Honua.Infrastructure.Events;
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
            Options.Create(CreateOptions()));

        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.Webhook, destination: "https://localhost/webhook"),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Contains("Webhook destination", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, handler.SendCount);
        httpClientFactory.DidNotReceive().CreateClient("alerts-webhook");
    }

    [UnitTest]
    public async Task DeliverAsync_WithControlCharactersInDedupeKey_SanitizesHeaders()
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var handler = new CapturingHeaderHandler();
        httpClientFactory.CreateClient("alerts-webhook").Returns(new HttpClient(handler));

        var sink = new WebhookAlertDeliverySink(
            httpClientFactory,
            Options.Create(CreateOptions()));

        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.Webhook, destination: "https://example.com/webhook"),
            AlertTestFixtures.CreateAlertEvent(dedupeKey: "evt-\r\n123"));

        Assert.True(result.Succeeded);
        Assert.Equal("evt-123", handler.AlertEventHeader);
        Assert.Equal("evt-123", handler.IdempotencyKeyHeader);
        Assert.False(string.IsNullOrWhiteSpace(handler.EventTimestampHeader));
        Assert.False(string.IsNullOrWhiteSpace(handler.SignatureHeader));
        var expectedSignature = "sha256=" + WebhookDeliveryHelper.ComputeSignature("signing-secret", handler.EventTimestampHeader!, "{\"test\":true}");
        Assert.Equal(expectedSignature, handler.SignatureHeader);
    }

    [UnitTest]
    public async Task DeliverAsync_WithoutSigningSecret_ReturnsNonRetryableFailure()
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var sink = new WebhookAlertDeliverySink(
            httpClientFactory,
            Options.Create(new AlertOptions()));

        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.Webhook, destination: "https://example.com/webhook"),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Contains("signing secret", result.Error, StringComparison.OrdinalIgnoreCase);
        httpClientFactory.DidNotReceive().CreateClient("alerts-webhook");
    }

    private static AlertOptions CreateOptions() =>
        new()
        {
            Dispatch = new AlertDispatchOptions
            {
                DefaultWebhookUrl = "https://example.com/webhook",
                DefaultWebhookSecret = "signing-secret"
            }
        };

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class CapturingHeaderHandler : HttpMessageHandler
    {
        public string? AlertEventHeader { get; private set; }

        public string? IdempotencyKeyHeader { get; private set; }

        public string? EventTimestampHeader { get; private set; }

        public string? SignatureHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AlertEventHeader = Assert.Single(request.Headers.GetValues("X-Honua-Alert-Event"));
            IdempotencyKeyHeader = Assert.Single(request.Headers.GetValues("Idempotency-Key"));
            EventTimestampHeader = Assert.Single(request.Headers.GetValues("X-Honua-Event-Timestamp"));
            SignatureHeader = Assert.Single(request.Headers.GetValues("X-Honua-Signature"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}

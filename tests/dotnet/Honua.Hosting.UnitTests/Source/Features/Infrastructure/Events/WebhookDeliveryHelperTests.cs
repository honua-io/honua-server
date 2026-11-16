// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Honua.Infrastructure.Events;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Events;

[Protocol(TestProtocols.TestQuality)]
public sealed class WebhookDeliveryHelperTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ComputeBackoffDelayMilliseconds_AppliesDeterministicJitterWithinBounds()
    {
        var request = CreateRequest("evt-1");

        var delayMs = WebhookDeliveryHelper.ComputeBackoffDelayMilliseconds(request, attempt: 3);

        Assert.InRange(delayMs, 324, 396);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ComputeBackoffDelayMilliseconds_RespectsMaximumBackoff()
    {
        var request = CreateRequest("evt-1") with
        {
            InitialBackoffMs = 500,
            MaxBackoffMs = 600
        };

        var delayMs = WebhookDeliveryHelper.ComputeBackoffDelayMilliseconds(request, attempt: 4);

        Assert.InRange(delayMs, 1, 600);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ComputeBackoffDelayMilliseconds_ProducesDifferentJitterForDifferentEvents()
    {
        var first = WebhookDeliveryHelper.ComputeBackoffDelayMilliseconds(CreateRequest("evt-1"), attempt: 2);
        var second = WebhookDeliveryHelper.ComputeBackoffDelayMilliseconds(CreateRequest("evt-2"), attempt: 2);

        Assert.NotEqual(first, second);
    }

    [UnitTheory]
    [Operation(Operations.TestInfrastructure)]
    [InlineData(200, "Delivered")]
    [InlineData(400, "Rejected")]
    [InlineData(405, "Rejected")]
    [InlineData(410, "Rejected")]
    [InlineData(307, "Failed")]
    [InlineData(308, "Failed")]
    [InlineData(408, "Failed")]
    [InlineData(429, "Failed")]
    [InlineData(503, "Failed")]
    public async Task DeliverWithRetryAsync_ClassifiesTheReceiverAnswer(int status, string expected)
    {
        using var handler = new FixedStatusHandler((HttpStatusCode)status);
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("test-webhook").Returns(httpClient);
        var request = CreateRequest("evt-1") with { MaxAttempts = 1 };

        var outcome = await WebhookDeliveryHelper.DeliverWithRetryAsync(request, httpClientFactory, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(expected, outcome.ToString());
    }

    private sealed class FixedStatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult<HttpResponseMessage>(new Honua.TestKit.CallerOwnedHttpResponseMessage(status));
    }

    private static WebhookDeliveryRequest CreateRequest(string eventId)
        => new()
        {
            Payload = "{}",
            EventId = eventId,
            Timestamp = DateTimeOffset.UtcNow,
            WebhookUri = new Uri("https://example.com/webhook"),
            Secret = "secret",
            HttpClientName = "test-webhook",
            MaxAttempts = 3,
            InitialBackoffMs = 100,
            MaxBackoffMs = 1000,
            RequestTimeoutSeconds = 5
        };
}

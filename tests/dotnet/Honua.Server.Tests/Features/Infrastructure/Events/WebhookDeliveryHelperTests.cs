// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Events;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

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

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;
using Honua.Server.Features.Alerts;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class AwsSqsAlertDeliverySinkTests
{
    private static AlertDeliveryOptions CreateOptionsWithSqs(string queueUrl = "https://sqs.us-east-1.amazonaws.com/123456/test-queue") =>
        new()
        {
            Dispatch = new AlertDeliveryDispatchOptions
            {
                AwsSqs = new AwsSqsChannelOptions { QueueUrl = queueUrl }
            }
        };

    [UnitTest]
    public async Task DeliverAsync_WithNoQueueUrlConfigured_ReturnsNonRetryableFailure()
    {
        var publisher = new FakeSqsPublisher();
        var sink = new AwsSqsAlertDeliverySink(publisher, Options.Create(new AlertDeliveryOptions()));

        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AwsSqs),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Contains("not configured", result.Error, StringComparison.Ordinal);
    }

    [UnitTest]
    public async Task DeliverAsync_WithSuccessfulSend_ReturnsSuccess()
    {
        var publisher = new FakeSqsPublisher
        {
            SendMessageAsyncHandler = static (_, _, _, _) => Task.FromResult(new SqsPublishResult(true, false, null))
        };

        var sink = new AwsSqsAlertDeliverySink(publisher, Options.Create(CreateOptionsWithSqs()));
        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AwsSqs),
            AlertTestFixtures.CreateAlertEvent());

        Assert.True(result.Succeeded);
        Assert.False(result.Retryable);
    }

    [UnitTest]
    public async Task DeliverAsync_WithServerError_ReturnsRetryableFailure()
    {
        var publisher = new FakeSqsPublisher
        {
            SendMessageAsyncHandler = static (_, _, _, _) => Task.FromResult(new SqsPublishResult(false, true, "SQS send responded with 500."))
        };

        var sink = new AwsSqsAlertDeliverySink(publisher, Options.Create(CreateOptionsWithSqs()));
        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AwsSqs),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.True(result.Retryable);
    }

    [UnitTest]
    public async Task DeliverAsync_WithQueueNotFoundError_ReturnsNonRetryableFailure()
    {
        var publisher = new FakeSqsPublisher
        {
            SendMessageAsyncHandler = static (_, _, _, _) => Task.FromResult(new SqsPublishResult(false, false, "SQS queue not found: test"))
        };

        var sink = new AwsSqsAlertDeliverySink(publisher, Options.Create(CreateOptionsWithSqs()));
        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AwsSqs),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Contains("queue not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [UnitTest]
    public async Task DeliverAsync_WithDestinationOverride_UsesDispatchDestination()
    {
        var publisher = new FakeSqsPublisher();

        var overrideUrl = "https://sqs.us-east-1.amazonaws.com/123456/override-queue";
        var sink = new AwsSqsAlertDeliverySink(publisher, Options.Create(CreateOptionsWithSqs()));

        await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AwsSqs, destination: overrideUrl),
            AlertTestFixtures.CreateAlertEvent());

        Assert.Equal(overrideUrl, publisher.LastQueueUrl);
    }

    [UnitTest]
    public async Task DeliverAsync_IncludesMessageAttributes()
    {
        var publisher = new FakeSqsPublisher();

        var sink = new AwsSqsAlertDeliverySink(publisher, Options.Create(CreateOptionsWithSqs()));
        await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AwsSqs),
            AlertTestFixtures.CreateAlertEvent());

        Assert.NotNull(publisher.LastAttributes);
        Assert.True(publisher.LastAttributes.ContainsKey("X-Honua-Alert-Rule"));
        Assert.True(publisher.LastAttributes.ContainsKey("X-Honua-Alert-Event"));
        Assert.True(publisher.LastAttributes.ContainsKey("X-Honua-Trigger-Type"));
        Assert.True(publisher.LastAttributes.ContainsKey("X-Honua-Severity"));
    }

    [UnitTest]
    public void ChannelType_ReturnsAwsSqs()
    {
        var publisher = new FakeSqsPublisher();
        var sink = new AwsSqsAlertDeliverySink(publisher, Options.Create(new AlertDeliveryOptions()));
        Assert.Equal(AlertChannelType.AwsSqs, sink.ChannelType);
    }
}

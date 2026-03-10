// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;
using Honua.Server.Features.Alerts;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class AwsSqsAlertDeliverySinkTests
{
    private static AlertOptions CreateOptionsWithSqs(string queueUrl = "https://sqs.us-east-1.amazonaws.com/123456/test-queue") =>
        new()
        {
            Dispatch = new AlertDispatchOptions
            {
                AwsSqs = new AwsSqsAlertOptions { QueueUrl = queueUrl }
            }
        };

    [UnitTest]
    public async Task DeliverAsync_WithNoQueueUrlConfigured_ReturnsNonRetryableFailure()
    {
        var publisher = Substitute.For<ISqsPublisher>();
        var sink = new AwsSqsAlertDeliverySink(publisher, Options.Create(new AlertOptions()));

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
        var publisher = Substitute.For<ISqsPublisher>();
        publisher.SendMessageAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new SqsPublishResult(true, false, null));

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
        var publisher = Substitute.For<ISqsPublisher>();
        publisher.SendMessageAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new SqsPublishResult(false, true, "SQS send responded with 500."));

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
        var publisher = Substitute.For<ISqsPublisher>();
        publisher.SendMessageAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new SqsPublishResult(false, false, "SQS queue not found: test"));

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
        string? capturedQueueUrl = null;
        var publisher = Substitute.For<ISqsPublisher>();
        publisher.SendMessageAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedQueueUrl = callInfo.ArgAt<string>(0);
                return new SqsPublishResult(true, false, null);
            });

        var overrideUrl = "https://sqs.us-east-1.amazonaws.com/123456/override-queue";
        var sink = new AwsSqsAlertDeliverySink(publisher, Options.Create(CreateOptionsWithSqs()));

        await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AwsSqs, destination: overrideUrl),
            AlertTestFixtures.CreateAlertEvent());

        Assert.Equal(overrideUrl, capturedQueueUrl);
    }

    [UnitTest]
    public async Task DeliverAsync_IncludesMessageAttributes()
    {
        Dictionary<string, string>? capturedAttributes = null;
        var publisher = Substitute.For<ISqsPublisher>();
        publisher.SendMessageAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedAttributes = callInfo.ArgAt<Dictionary<string, string>>(2);
                return new SqsPublishResult(true, false, null);
            });

        var sink = new AwsSqsAlertDeliverySink(publisher, Options.Create(CreateOptionsWithSqs()));
        await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AwsSqs),
            AlertTestFixtures.CreateAlertEvent());

        Assert.NotNull(capturedAttributes);
        Assert.True(capturedAttributes.ContainsKey("X-Honua-Alert-Rule"));
        Assert.True(capturedAttributes.ContainsKey("X-Honua-Alert-Event"));
        Assert.True(capturedAttributes.ContainsKey("X-Honua-Trigger-Type"));
        Assert.True(capturedAttributes.ContainsKey("X-Honua-Severity"));
    }

    [UnitTest]
    public void ChannelType_ReturnsAwsSqs()
    {
        var publisher = Substitute.For<ISqsPublisher>();
        var sink = new AwsSqsAlertDeliverySink(publisher, Options.Create(new AlertOptions()));
        Assert.Equal(AlertChannelType.AwsSqs, sink.ChannelType);
    }
}

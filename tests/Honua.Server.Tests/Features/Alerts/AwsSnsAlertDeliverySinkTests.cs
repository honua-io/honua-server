// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;
using Honua.Server.Features.Alerts;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class AwsSnsAlertDeliverySinkTests
{
    private static AlertOptions CreateOptionsWithSns(string topicArn = "arn:aws:sns:us-east-1:123456:test-topic") =>
        new()
        {
            Dispatch = new AlertDispatchOptions
            {
                AwsSns = new AwsSnsAlertOptions { TopicArn = topicArn }
            }
        };

    [UnitTest]
    public async Task DeliverAsync_WithNoTopicArnConfigured_ReturnsNonRetryableFailure()
    {
        var publisher = Substitute.For<ISnsPublisher>();
        var sink = new AwsSnsAlertDeliverySink(publisher, Options.Create(new AlertOptions()));

        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AwsSns),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Contains("not configured", result.Error, StringComparison.Ordinal);
    }

    [UnitTest]
    public async Task DeliverAsync_WithSuccessfulPublish_ReturnsSuccess()
    {
        var publisher = Substitute.For<ISnsPublisher>();
        publisher.PublishAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new SnsPublishResult(true, false, null));

        var sink = new AwsSnsAlertDeliverySink(publisher, Options.Create(CreateOptionsWithSns()));
        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AwsSns),
            AlertTestFixtures.CreateAlertEvent());

        Assert.True(result.Succeeded);
        Assert.False(result.Retryable);
    }

    [UnitTest]
    public async Task DeliverAsync_WithServerError_ReturnsRetryableFailure()
    {
        var publisher = Substitute.For<ISnsPublisher>();
        publisher.PublishAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new SnsPublishResult(false, true, "SNS publish responded with 500."));

        var sink = new AwsSnsAlertDeliverySink(publisher, Options.Create(CreateOptionsWithSns()));
        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AwsSns),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.True(result.Retryable);
    }

    [UnitTest]
    public async Task DeliverAsync_WithAuthorizationError_ReturnsNonRetryableFailure()
    {
        var publisher = Substitute.For<ISnsPublisher>();
        publisher.PublishAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new SnsPublishResult(false, false, "SNS authorization failed: Access denied"));

        var sink = new AwsSnsAlertDeliverySink(publisher, Options.Create(CreateOptionsWithSns()));
        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AwsSns),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Contains("authorization", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [UnitTest]
    public async Task DeliverAsync_WithDestinationOverride_UsesDispatchDestination()
    {
        string? capturedTopicArn = null;
        var publisher = Substitute.For<ISnsPublisher>();
        publisher.PublishAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedTopicArn = callInfo.ArgAt<string>(0);
                return new SnsPublishResult(true, false, null);
            });

        var overrideArn = "arn:aws:sns:us-east-1:123456:override-topic";
        var sink = new AwsSnsAlertDeliverySink(publisher, Options.Create(
            CreateOptionsWithSns("arn:aws:sns:us-east-1:123456:default-topic")));

        await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AwsSns, destination: overrideArn),
            AlertTestFixtures.CreateAlertEvent());

        Assert.Equal(overrideArn, capturedTopicArn);
    }

    [UnitTest]
    public async Task DeliverAsync_IncludesMessageAttributes()
    {
        Dictionary<string, string>? capturedAttributes = null;
        var publisher = Substitute.For<ISnsPublisher>();
        publisher.PublishAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedAttributes = callInfo.ArgAt<Dictionary<string, string>>(3);
                return new SnsPublishResult(true, false, null);
            });

        var sink = new AwsSnsAlertDeliverySink(publisher, Options.Create(CreateOptionsWithSns()));
        await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AwsSns),
            AlertTestFixtures.CreateAlertEvent());

        Assert.NotNull(capturedAttributes);
        Assert.True(capturedAttributes.ContainsKey("X-Honua-Alert-Rule"));
        Assert.True(capturedAttributes.ContainsKey("X-Honua-Alert-Event"));
        Assert.True(capturedAttributes.ContainsKey("X-Honua-Trigger-Type"));
        Assert.True(capturedAttributes.ContainsKey("X-Honua-Severity"));
    }

    [UnitTest]
    public void ChannelType_ReturnsAwsSns()
    {
        var publisher = Substitute.For<ISnsPublisher>();
        var sink = new AwsSnsAlertDeliverySink(publisher, Options.Create(new AlertOptions()));
        Assert.Equal(AlertChannelType.AwsSns, sink.ChannelType);
    }
}

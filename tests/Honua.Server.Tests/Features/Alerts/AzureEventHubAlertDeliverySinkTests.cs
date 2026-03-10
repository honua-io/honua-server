// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;
using Honua.Server.Features.Alerts;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class AzureEventHubAlertDeliverySinkTests
{
    private static AlertOptions CreateOptionsWithEventHub() =>
        new()
        {
            Dispatch = new AlertDispatchOptions
            {
                AzureEventHub = new AzureEventHubAlertOptions
                {
                    ConnectionString = "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=xxx",
                    EventHubName = "test-hub"
                }
            }
        };

    [UnitTest]
    public async Task DeliverAsync_WithNoConnectionStringConfigured_ReturnsNonRetryableFailure()
    {
        var publisher = Substitute.For<IEventHubPublisher>();
        var sink = new AzureEventHubAlertDeliverySink(publisher, Options.Create(new AlertOptions()));

        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AzureEventHub),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Contains("not configured", result.Error, StringComparison.Ordinal);
    }

    [UnitTest]
    public async Task DeliverAsync_WithSuccessfulSend_ReturnsSuccess()
    {
        var publisher = Substitute.For<IEventHubPublisher>();
        publisher.SendAsync(
                Arg.Any<BinaryData>(),
                Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(new EventHubPublishResult(true, false, null));

        var sink = new AzureEventHubAlertDeliverySink(publisher, Options.Create(CreateOptionsWithEventHub()));
        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AzureEventHub),
            AlertTestFixtures.CreateAlertEvent());

        Assert.True(result.Succeeded);
        Assert.False(result.Retryable);
    }

    [UnitTest]
    public async Task DeliverAsync_WithTransientError_ReturnsRetryableFailure()
    {
        var publisher = Substitute.For<IEventHubPublisher>();
        publisher.SendAsync(
                Arg.Any<BinaryData>(),
                Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(new EventHubPublishResult(false, true, "Event Hub transient error: test"));

        var sink = new AzureEventHubAlertDeliverySink(publisher, Options.Create(CreateOptionsWithEventHub()));
        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AzureEventHub),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.True(result.Retryable);
    }

    [UnitTest]
    public async Task DeliverAsync_WithResourceNotFound_ReturnsNonRetryableFailure()
    {
        var publisher = Substitute.For<IEventHubPublisher>();
        publisher.SendAsync(
                Arg.Any<BinaryData>(),
                Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(new EventHubPublishResult(false, false, "Event Hub not found: test"));

        var sink = new AzureEventHubAlertDeliverySink(publisher, Options.Create(CreateOptionsWithEventHub()));
        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AzureEventHub),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [UnitTest]
    public async Task DeliverAsync_IncludesEventProperties()
    {
        Dictionary<string, object>? capturedProperties = null;
        var publisher = Substitute.For<IEventHubPublisher>();
        publisher.SendAsync(
                Arg.Any<BinaryData>(),
                Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedProperties = callInfo.ArgAt<Dictionary<string, object>>(1);
                return new EventHubPublishResult(true, false, null);
            });

        var sink = new AzureEventHubAlertDeliverySink(publisher, Options.Create(CreateOptionsWithEventHub()));
        await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AzureEventHub),
            AlertTestFixtures.CreateAlertEvent());

        Assert.NotNull(capturedProperties);
        Assert.True(capturedProperties.ContainsKey("X-Honua-Alert-Rule"));
        Assert.True(capturedProperties.ContainsKey("X-Honua-Alert-Event"));
        Assert.True(capturedProperties.ContainsKey("X-Honua-Trigger-Type"));
        Assert.True(capturedProperties.ContainsKey("X-Honua-Severity"));
        Assert.True(capturedProperties.ContainsKey("X-Honua-Incident-Status"));
    }

    [UnitTest]
    public void ChannelType_ReturnsAzureEventHub()
    {
        var publisher = Substitute.For<IEventHubPublisher>();
        var sink = new AzureEventHubAlertDeliverySink(publisher, Options.Create(new AlertOptions()));
        Assert.Equal(AlertChannelType.AzureEventHub, sink.ChannelType);
    }
}

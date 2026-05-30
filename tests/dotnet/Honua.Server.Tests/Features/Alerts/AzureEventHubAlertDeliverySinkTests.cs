// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;
using Honua.Alerts;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class AzureEventHubAlertDeliverySinkTests
{
    private static AlertDeliveryOptions CreateOptionsWithEventHub() =>
        new()
        {
            Dispatch = new AlertDeliveryDispatchOptions
            {
                AzureEventHub = new AzureEventHubChannelOptions
                {
                    ConnectionString = "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=xxx",
                    EventHubName = "test-hub"
                }
            }
        };

    [UnitTest]
    public async Task DeliverAsync_WithNoConnectionStringConfigured_ReturnsNonRetryableFailure()
    {
        var publisher = new FakeEventHubPublisher();
        var sink = new AzureEventHubAlertDeliverySink(publisher, Options.Create(new AlertDeliveryOptions()));

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
        var publisher = new FakeEventHubPublisher
        {
            SendAsyncHandler = static (_, _, _) => Task.FromResult(new EventHubPublishResult(true, false, null))
        };

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
        var publisher = new FakeEventHubPublisher
        {
            SendAsyncHandler = static (_, _, _) => Task.FromResult(new EventHubPublishResult(false, true, "Event Hub transient error: test"))
        };

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
        var publisher = new FakeEventHubPublisher
        {
            SendAsyncHandler = static (_, _, _) => Task.FromResult(new EventHubPublishResult(false, false, "Event Hub not found: test"))
        };

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
        var publisher = new FakeEventHubPublisher();

        var sink = new AzureEventHubAlertDeliverySink(publisher, Options.Create(CreateOptionsWithEventHub()));
        await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AzureEventHub),
            AlertTestFixtures.CreateAlertEvent());

        Assert.NotNull(publisher.LastProperties);
        Assert.True(publisher.LastProperties.ContainsKey("X-Honua-Alert-Rule"));
        Assert.True(publisher.LastProperties.ContainsKey("X-Honua-Alert-Event"));
        Assert.True(publisher.LastProperties.ContainsKey("X-Honua-Trigger-Type"));
        Assert.True(publisher.LastProperties.ContainsKey("X-Honua-Severity"));
        Assert.True(publisher.LastProperties.ContainsKey("X-Honua-Incident-Status"));
    }

    [UnitTest]
    public void ChannelType_ReturnsAzureEventHub()
    {
        var publisher = new FakeEventHubPublisher();
        var sink = new AzureEventHubAlertDeliverySink(publisher, Options.Create(new AlertDeliveryOptions()));
        Assert.Equal(AlertChannelType.AzureEventHub, sink.ChannelType);
    }
}

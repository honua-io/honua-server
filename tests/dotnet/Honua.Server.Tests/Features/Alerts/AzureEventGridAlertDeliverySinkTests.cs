// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Azure.Messaging.EventGrid;
using Honua.Core.Features.Alerts.Domain;
using Honua.Alerts;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class AzureEventGridAlertDeliverySinkTests
{
    [UnitTest]
    public async Task DeliverAsync_WithNoEndpointConfigured_ReturnsNonRetryableFailure()
    {
        // EventGridPublisherClient cannot be mocked (no interface), so we test the
        // configuration guard path which does not require a real client.
        var client = CreateDummyClient();
        var sink = new AzureEventGridAlertDeliverySink(
            client,
            Options.Create(new AlertDeliveryOptions()));

        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AzureEventGrid),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Contains("not configured", result.Error, StringComparison.Ordinal);
    }

    [UnitTest]
    public void ChannelType_ReturnsAzureEventGrid()
    {
        var client = CreateDummyClient();
        var sink = new AzureEventGridAlertDeliverySink(client, Options.Create(new AlertDeliveryOptions()));

        Assert.Equal(AlertChannelType.AzureEventGrid, sink.ChannelType);
    }

    /// <summary>
    /// Creates a dummy client pointing at a non-existent endpoint for unit tests
    /// that only exercise configuration guard paths.
    /// </summary>
    private static EventGridPublisherClient CreateDummyClient()
    {
        return new EventGridPublisherClient(
            new Uri("https://dummy.eventgrid.azure.net/api/events"),
            new Azure.AzureKeyCredential("dummykey"));
    }
}

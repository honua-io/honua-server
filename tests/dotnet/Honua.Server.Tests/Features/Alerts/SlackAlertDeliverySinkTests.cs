// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Alerts.Domain;
using Honua.Alerts;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class SlackAlertDeliverySinkTests
{
    private static AlertDeliveryOptions CreateOptionsWithSlack(string webhookUrl = "https://hooks.slack.com/services/T00/B00/xxx") =>
        new()
        {
            Dispatch = new AlertDeliveryDispatchOptions
            {
                Slack = new SlackChannelOptions { WebhookUrl = webhookUrl }
            }
        };

    [UnitTest]
    public async Task DeliverAsync_WithNoWebhookUrlConfigured_ReturnsNonRetryableFailure()
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var sink = new SlackAlertDeliverySink(httpClientFactory, Options.Create(new AlertDeliveryOptions()));

        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.Slack),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Contains("not configured", result.Error, StringComparison.Ordinal);
    }

    [UnitTest]
    public async Task DeliverAsync_WithSuccessfulPost_ReturnsSuccess()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK);
        var client = new HttpClient(handler);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("alerts-slack").Returns(client);

        var sink = new SlackAlertDeliverySink(httpClientFactory, Options.Create(CreateOptionsWithSlack()));
        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.Slack),
            AlertTestFixtures.CreateAlertEvent());

        Assert.True(result.Succeeded);
        Assert.False(result.Retryable);
    }

    [UnitTest]
    public async Task DeliverAsync_WithServerError_ReturnsRetryableFailure()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.InternalServerError);
        var client = new HttpClient(handler);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("alerts-slack").Returns(client);

        var sink = new SlackAlertDeliverySink(httpClientFactory, Options.Create(CreateOptionsWithSlack()));
        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.Slack),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.True(result.Retryable);
    }

    [UnitTest]
    public async Task DeliverAsync_WithRateLimitResponse_ReturnsRetryableFailure()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.TooManyRequests);
        var client = new HttpClient(handler);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("alerts-slack").Returns(client);

        var sink = new SlackAlertDeliverySink(httpClientFactory, Options.Create(CreateOptionsWithSlack()));
        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.Slack),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.True(result.Retryable);
    }

    [UnitTest]
    public void ChannelType_ReturnsSlack()
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var sink = new SlackAlertDeliverySink(httpClientFactory, Options.Create(new AlertDeliveryOptions()));
        Assert.Equal(AlertChannelType.Slack, sink.ChannelType);
    }
}

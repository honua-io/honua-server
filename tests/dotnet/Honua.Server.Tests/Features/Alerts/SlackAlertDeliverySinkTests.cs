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
    // Destination is an IP literal so the sink's outbound SSRF guard does not perform a live DNS
    // lookup; see AlertTestFixtures.RoutableWebhookBaseUrl (#3056).
    private static AlertDeliveryOptions CreateOptionsWithSlack(
        string webhookUrl = AlertTestFixtures.RoutableWebhookBaseUrl + "/services/T00/B00/xxx") =>
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
        using var client = new HttpClient(handler);
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
        using var client = new HttpClient(handler);
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
        using var client = new HttpClient(handler);
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
    public async Task DeliverAsync_WithOpsEvent_RendersOpsTitleAndOperationIdNotRuleZero()
    {
        var handler = new CapturingHttpMessageHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("alerts-slack").Returns(client);

        var sink = new SlackAlertDeliverySink(httpClientFactory, Options.Create(CreateOptionsWithSlack()));
        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.Slack),
            AlertTestFixtures.CreateOpsAlertEvent());

        Assert.True(result.Succeeded);
        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("Deploy prod-web failed", handler.LastRequestBody, StringComparison.Ordinal);
        Assert.Contains("op-9f3c", handler.LastRequestBody, StringComparison.Ordinal);
        Assert.Contains("Critical", handler.LastRequestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Rule:", handler.LastRequestBody, StringComparison.Ordinal);
    }

    [UnitTest]
    public async Task DeliverAsync_WithTransientResolutionFailure_ReturnsRetryableFailure()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("alerts-slack").Returns(client);

        var sink = new SlackAlertDeliverySink(
            httpClientFactory,
            Options.Create(CreateOptionsWithSlack(AlertTestFixtures.HostnameWebhookBaseUrl + "/services/T00/B00/xxx")),
            destinationGuard: AlertTestFixtures.GuardWithUnavailableResolver());

        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.Slack),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.True(result.Retryable);
        httpClientFactory.DidNotReceive().CreateClient("alerts-slack");
    }

    [UnitTest]
    public async Task DeliverAsync_WithWebhookUrlResolvingToPrivateAddress_ReturnsNonRetryableFailure()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("alerts-slack").Returns(client);

        var sink = new SlackAlertDeliverySink(
            httpClientFactory,
            Options.Create(CreateOptionsWithSlack(AlertTestFixtures.HostnameWebhookBaseUrl + "/services/T00/B00/xxx")),
            destinationGuard: AlertTestFixtures.GuardResolvingTo("10.0.0.5"));

        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.Slack),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        httpClientFactory.DidNotReceive().CreateClient("alerts-slack");
    }

    [UnitTest]
    public void ChannelType_ReturnsSlack()
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var sink = new SlackAlertDeliverySink(httpClientFactory, Options.Create(new AlertDeliveryOptions()));
        Assert.Equal(AlertChannelType.Slack, sink.ChannelType);
    }
}

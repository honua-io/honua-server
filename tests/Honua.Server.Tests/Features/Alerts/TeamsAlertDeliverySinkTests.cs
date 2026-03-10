// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.Core.Features.Alerts.Domain;
using Honua.Server.Features.Alerts;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class TeamsAlertDeliverySinkTests
{
    private static AlertOptions CreateOptionsWithTeams(string webhookUrl = "https://outlook.office.com/webhook/xxx") =>
        new()
        {
            Dispatch = new AlertDispatchOptions
            {
                Teams = new TeamsAlertOptions { WebhookUrl = webhookUrl }
            }
        };

    [UnitTest]
    public async Task DeliverAsync_WithNoWebhookUrlConfigured_ReturnsNonRetryableFailure()
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var sink = new TeamsAlertDeliverySink(httpClientFactory, Options.Create(new AlertOptions()));

        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.MicrosoftTeams),
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
        httpClientFactory.CreateClient("alerts-teams").Returns(client);

        var sink = new TeamsAlertDeliverySink(httpClientFactory, Options.Create(CreateOptionsWithTeams()));
        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.MicrosoftTeams),
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
        httpClientFactory.CreateClient("alerts-teams").Returns(client);

        var sink = new TeamsAlertDeliverySink(httpClientFactory, Options.Create(CreateOptionsWithTeams()));
        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.MicrosoftTeams),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.True(result.Retryable);
    }

    [UnitTest]
    public async Task DeliverAsync_UsesCanonicalMessageCardProperties()
    {
        var handler = new CapturingHttpMessageHandler(HttpStatusCode.OK);
        var client = new HttpClient(handler);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("alerts-teams").Returns(client);

        var sink = new TeamsAlertDeliverySink(httpClientFactory, Options.Create(CreateOptionsWithTeams()));

        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.MicrosoftTeams),
            AlertTestFixtures.CreateAlertEvent());

        Assert.True(result.Succeeded);
        Assert.NotNull(handler.LastRequestBody);

        using var document = JsonDocument.Parse(handler.LastRequestBody);
        Assert.Equal("MessageCard", document.RootElement.GetProperty("@type").GetString());
        Assert.Equal("https://schema.org/extensions", document.RootElement.GetProperty("@context").GetString());
    }

    [UnitTest]
    public void ChannelType_ReturnsMicrosoftTeams()
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var sink = new TeamsAlertDeliverySink(httpClientFactory, Options.Create(new AlertOptions()));
        Assert.Equal(AlertChannelType.MicrosoftTeams, sink.ChannelType);
    }

    private sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        public CapturingHttpMessageHandler(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        public string LastRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(_statusCode);
        }
    }
}

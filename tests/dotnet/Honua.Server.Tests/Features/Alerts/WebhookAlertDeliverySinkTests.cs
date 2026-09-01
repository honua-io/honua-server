// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Alerts;
using Honua.Infrastructure.Events;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class WebhookAlertDeliverySinkTests
{
    [UnitTest]
    public async Task DeliverAsync_WhenSecretRotates_UsesNewValueWithoutRestartAndDoesNotLeakIt()
    {
        const string firstSecret = "rotation-secret-v1";
        const string secondSecret = "rotation-secret-v2";
        var activeSecret = firstSecret;
        var secretProvider = Substitute.For<Honua.Core.Features.Security.Abstractions.ISecretProvider>();
        secretProvider.IsSecretReference(AlertTestFixtures.SecretReference).Returns(true);
        secretProvider.GetSecretAsync(AlertTestFixtures.SecretReference, Arg.Any<CancellationToken>())
            .Returns(_ => activeSecret);

        var handler = new CapturingHeaderHandler();
        using var httpClient = new HttpClient(handler);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("alerts-webhook").Returns(httpClient);
        var sink = new WebhookAlertDeliverySink(httpClientFactory, Options.Create(CreateOptions()), secretProvider);
        var dispatch = AlertTestFixtures.CreateDispatchItem(
            AlertChannelType.Webhook,
            AlertTestFixtures.RoutableWebhookBaseUrl + "/webhook");

        (await sink.DeliverAsync(dispatch, AlertTestFixtures.CreateAlertEvent())).Succeeded.Should().BeTrue();
        activeSecret = secondSecret;
        (await sink.DeliverAsync(dispatch, AlertTestFixtures.CreateAlertEvent())).Succeeded.Should().BeTrue();

        handler.Signatures.Should().HaveCount(2);
        handler.Signatures[0].Should().Be("sha256=" + WebhookDeliveryHelper.ComputeSignature(firstSecret, handler.Timestamps[0], "{\"test\":true}"));
        handler.Signatures[1].Should().Be("sha256=" + WebhookDeliveryHelper.ComputeSignature(secondSecret, handler.Timestamps[1], "{\"test\":true}"));
        handler.Signatures.Should().OnlyContain(signature => !signature.Contains(firstSecret, StringComparison.Ordinal) && !signature.Contains(secondSecret, StringComparison.Ordinal));
        await secretProvider.Received(2).GetSecretAsync(AlertTestFixtures.SecretReference, Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task DeliverAsync_WithPlaintextSigningSecret_RejectsWithoutResolving()
    {
        var options = CreateOptions();
        options.Dispatch.DefaultWebhookSecretReference = "plaintext-secret";
        var secretProvider = Substitute.For<Honua.Core.Features.Security.Abstractions.ISecretProvider>();
        var sink = new WebhookAlertDeliverySink(
            Substitute.For<IHttpClientFactory>(),
            Options.Create(options),
            secretProvider);

        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.Webhook),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Contains("secret reference", result.Error, StringComparison.OrdinalIgnoreCase);
        await secretProvider.DidNotReceiveWithAnyArgs().GetSecretAsync(default!, default);
    }

    [UnitTest]
    public async Task DeliverAsync_WithUnsafeDestination_DoesNotSendRequest()
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var handler = new CountingHandler();
        using var httpClient = new HttpClient(handler);
        httpClientFactory.CreateClient("alerts-webhook").Returns(httpClient);

        var sink = new WebhookAlertDeliverySink(
            httpClientFactory,
            Options.Create(CreateOptions()),
            AlertTestFixtures.SecretProvider("signing-secret"));

        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.Webhook, destination: "https://localhost/webhook"),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Contains("Webhook destination", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, handler.SendCount);
        httpClientFactory.DidNotReceive().CreateClient("alerts-webhook");
    }

    [UnitTest]
    public async Task DeliverAsync_WithTransientResolutionFailure_ReturnsRetryableFailure()
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var handler = new CountingHandler();
        using var httpClient = new HttpClient(handler);
        httpClientFactory.CreateClient("alerts-webhook").Returns(httpClient);

        var sink = new WebhookAlertDeliverySink(
            httpClientFactory,
            Options.Create(CreateOptions()),
            AlertTestFixtures.SecretProvider("signing-secret"),
            AlertTestFixtures.GuardWithUnavailableResolver());

        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(
                AlertChannelType.Webhook,
                destination: AlertTestFixtures.HostnameWebhookBaseUrl + "/webhook"),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.True(result.Retryable);
        Assert.Equal(0, handler.SendCount);
    }

    [UnitTest]
    public async Task DeliverAsync_WithDestinationResolvingToPrivateAddress_ReturnsNonRetryableFailure()
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var handler = new CountingHandler();
        using var httpClient = new HttpClient(handler);
        httpClientFactory.CreateClient("alerts-webhook").Returns(httpClient);

        var sink = new WebhookAlertDeliverySink(
            httpClientFactory,
            Options.Create(CreateOptions()),
            AlertTestFixtures.SecretProvider("signing-secret"),
            AlertTestFixtures.GuardResolvingTo("10.0.0.5"));

        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(
                AlertChannelType.Webhook,
                destination: AlertTestFixtures.HostnameWebhookBaseUrl + "/webhook"),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Equal(0, handler.SendCount);
    }

    [UnitTest]
    public async Task DeliverAsync_WithControlCharactersInDedupeKey_SanitizesHeaders()
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var handler = new CapturingHeaderHandler();
        using var httpClient = new HttpClient(handler);
        httpClientFactory.CreateClient("alerts-webhook").Returns(httpClient);

        var sink = new WebhookAlertDeliverySink(
            httpClientFactory,
            Options.Create(CreateOptions()),
            AlertTestFixtures.SecretProvider("signing-secret"));

        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(
                AlertChannelType.Webhook,
                destination: AlertTestFixtures.RoutableWebhookBaseUrl + "/webhook"),
            AlertTestFixtures.CreateAlertEvent(dedupeKey: "evt-\r\n123"));

        Assert.True(result.Succeeded);
        Assert.Equal("evt-123", handler.AlertEventHeader);
        Assert.Equal("evt-123", handler.IdempotencyKeyHeader);
        Assert.False(string.IsNullOrWhiteSpace(handler.EventTimestampHeader));
        Assert.False(string.IsNullOrWhiteSpace(handler.SignatureHeader));
        var expectedSignature = "sha256=" + WebhookDeliveryHelper.ComputeSignature("signing-secret", handler.EventTimestampHeader!, "{\"test\":true}");
        Assert.Equal(expectedSignature, handler.SignatureHeader);
    }

    [UnitTest]
    public async Task DeliverAsync_WithoutSigningSecret_ReturnsNonRetryableFailure()
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var sink = new WebhookAlertDeliverySink(
            httpClientFactory,
            Options.Create(new AlertOptions()),
            AlertTestFixtures.SecretProvider("unused"));

        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.Webhook, destination: "https://example.com/webhook"),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Contains("signing secret", result.Error, StringComparison.OrdinalIgnoreCase);
        httpClientFactory.DidNotReceive().CreateClient("alerts-webhook");
    }

    private static AlertOptions CreateOptions() =>
        new()
        {
            Dispatch = new AlertDispatchOptions
            {
                // IP literal keeps the outbound SSRF guard off live DNS; see
                // AlertTestFixtures.RoutableWebhookBaseUrl (#3056).
                DefaultWebhookUrl = AlertTestFixtures.RoutableWebhookBaseUrl + "/webhook",
                DefaultWebhookSecretReference = AlertTestFixtures.SecretReference
            }
        };

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            // Ownership transfers to the HttpClient pipeline that invoked SendAsync;
            // it disposes the response after the caller finishes with it.
            return Task.FromResult<System.Net.Http.HttpResponseMessage>(new Honua.TestKit.CallerOwnedHttpResponseMessage(HttpStatusCode.OK));
        }
    }
    private sealed class CapturingHeaderHandler : HttpMessageHandler
    {
        public List<string> Signatures { get; } = [];

        public List<string> Timestamps { get; } = [];

        public string? AlertEventHeader { get; private set; }

        public string? IdempotencyKeyHeader { get; private set; }

        public string? EventTimestampHeader { get; private set; }

        public string? SignatureHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AlertEventHeader = Assert.Single(request.Headers.GetValues("X-Honua-Alert-Event"));
            IdempotencyKeyHeader = Assert.Single(request.Headers.GetValues("Idempotency-Key"));
            EventTimestampHeader = Assert.Single(request.Headers.GetValues("X-Honua-Event-Timestamp"));
            SignatureHeader = Assert.Single(request.Headers.GetValues("X-Honua-Signature"));
            Timestamps.Add(EventTimestampHeader);
            Signatures.Add(SignatureHeader);
            // Ownership transfers to the HttpClient pipeline that invoked SendAsync;
            // it disposes the response after the caller finishes with it.
            return Task.FromResult<System.Net.Http.HttpResponseMessage>(new Honua.TestKit.CallerOwnedHttpResponseMessage(HttpStatusCode.OK));
        }
    }
}

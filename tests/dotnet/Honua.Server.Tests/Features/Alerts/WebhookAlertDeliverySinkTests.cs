// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Configuration;
using Honua.Core.Features.Security.Abstractions;
using Honua.Alerts;
using Honua.Infrastructure.Configuration;
using Honua.Infrastructure.Events;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class WebhookAlertDeliverySinkTests
{
    [UnitTest]
    public async Task DeliverAsync_WithUnsafeDestination_DoesNotSendRequest()
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var handler = new CountingHandler();
        using var httpClient = new HttpClient(handler);
        httpClientFactory.CreateClient("alerts-webhook").Returns(httpClient);

        var sink = new WebhookAlertDeliverySink(
            httpClientFactory,
            Options.Create(CreateOptions()));

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
            Options.Create(CreateOptions()));

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
            Options.Create(new AlertOptions()));

        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.Webhook, destination: "https://example.com/webhook"),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Contains("signing secret", result.Error, StringComparison.OrdinalIgnoreCase);
        httpClientFactory.DidNotReceive().CreateClient("alerts-webhook");
    }

    [UnitTest]
    public async Task DeliverAsync_ProductionSecretCache_ServesCachedSecretUntilCacheDuration()
    {
        const string secretReference = "env:WEBHOOK_SIGNING_SECRET";
        const string secretV1 = "webhook-secret-v1";
        const string secretV2 = "webhook-secret-v2";
        var started = new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(started);
        var resolver = new RotatingConnectionSecretResolver { Value = secretV1 };
        var logs = new CollectingLogger();
        using var secrets = new SecretProvider(
            resolver,
            Options.Create(new SecretProviderOptions
            {
                EnableCaching = true,
                CacheDuration = TimeSpan.FromMinutes(5),
                LogSecretAccess = true,
                OperationTimeout = TimeSpan.FromSeconds(5)
            }),
            logs,
            clock);

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var handler = new CapturingHeaderHandler();
        using var httpClient = new HttpClient(handler);
        httpClientFactory.CreateClient("alerts-webhook").Returns(httpClient);
        var sink = new WebhookAlertDeliverySink(
            httpClientFactory,
            Options.Create(new AlertOptions
            {
                Dispatch = new AlertDispatchOptions
                {
                    DefaultWebhookUrl = AlertTestFixtures.RoutableWebhookBaseUrl + "/webhook",
                    DefaultWebhookSecret = secretReference
                }
            }),
            secretProvider: secrets);
        var item = AlertTestFixtures.CreateDispatchItem(
            AlertChannelType.Webhook,
            destination: AlertTestFixtures.RoutableWebhookBaseUrl + "/webhook");
        var alert = AlertTestFixtures.CreateAlertEvent();

        var warmed = await sink.DeliverAsync(item, alert);
        AssertSigned(warmed, handler, secretV1, alert.PayloadJson);
        Assert.Equal(1, resolver.ResolveCalls);

        resolver.Value = secretV2;
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
        var duringWindow = await sink.DeliverAsync(item, alert);
        AssertSigned(duringWindow, handler, secretV1, alert.PayloadJson);
        Assert.Equal(1, resolver.ResolveCalls);

        resolver.Fail = true;
        var duringOutage = await sink.DeliverAsync(item, alert);
        AssertSigned(duringOutage, handler, secretV1, alert.PayloadJson);
        Assert.Equal(1, resolver.ResolveCalls);
        Assert.DoesNotContain("unsigned", duringOutage.Error ?? "", StringComparison.OrdinalIgnoreCase);

        clock.Advance(TimeSpan.FromTicks(1));
        resolver.Fail = false;
        var afterDuration = await sink.DeliverAsync(item, alert);
        AssertSigned(afterDuration, handler, secretV2, alert.PayloadJson);
        Assert.Equal(2, resolver.ResolveCalls);

        resolver.Value = secretV1;
        resolver.Fail = true;
        var later = await sink.DeliverAsync(item, alert);
        AssertSigned(later, handler, secretV2, alert.PayloadJson);
        Assert.Equal(2, resolver.ResolveCalls);

        var signatureBeforeExpiredOutage = handler.SignatureHeader;
        clock.Advance(TimeSpan.FromMinutes(5));
        var expiredOutage = await sink.DeliverAsync(item, alert);
        Assert.False(expiredOutage.Succeeded);
        Assert.True(expiredOutage.Retryable);
        Assert.Equal("Webhook signing secret could not be resolved.", expiredOutage.Error);
        Assert.Equal(3, resolver.ResolveCalls);
        Assert.Equal(signatureBeforeExpiredOutage, handler.SignatureHeader);

        var leaked = string.Join('\n', logs.Messages);
        Assert.DoesNotContain(secretV1, leaked, StringComparison.Ordinal);
        Assert.DoesNotContain(secretV2, leaked, StringComparison.Ordinal);
        Assert.DoesNotContain(secretReference, leaked, StringComparison.Ordinal);
        foreach (var result in new[] { warmed, duringWindow, duringOutage, afterDuration, later, expiredOutage })
        {
            Assert.DoesNotContain(secretV1, result.Error ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain(secretV2, result.Error ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain(secretReference, result.Error ?? "", StringComparison.Ordinal);
        }
    }

    private static void AssertSigned(AlertDeliveryResult result, CapturingHeaderHandler handler, string secret, string payload)
    {
        Assert.True(result.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(handler.SignatureHeader));
        Assert.False(string.IsNullOrWhiteSpace(handler.EventTimestampHeader));
        var expected = "sha256=" + WebhookDeliveryHelper.ComputeSignature(secret, handler.EventTimestampHeader!, payload);
        Assert.Equal(expected, handler.SignatureHeader);
        Assert.DoesNotContain(secret, handler.SignatureHeader, StringComparison.Ordinal);
    }

    private static AlertOptions CreateOptions() =>
        new()
        {
            Dispatch = new AlertDispatchOptions
            {
                // IP literal keeps the outbound SSRF guard off live DNS; see
                // AlertTestFixtures.RoutableWebhookBaseUrl (#3056).
                DefaultWebhookUrl = AlertTestFixtures.RoutableWebhookBaseUrl + "/webhook",
                DefaultWebhookSecret = "signing-secret"
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
            // Ownership transfers to the HttpClient pipeline that invoked SendAsync;
            // it disposes the response after the caller finishes with it.
            return Task.FromResult<System.Net.Http.HttpResponseMessage>(new Honua.TestKit.CallerOwnedHttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class RotatingConnectionSecretResolver : IConnectionSecretResolver
    {
        public string Value { get; set; } = "";

        public bool Fail { get; set; }

        public int ResolveCalls { get; private set; }

        public string ProviderName => "env";

        public bool CanResolve(string secretKey)
            => secretKey.StartsWith("env:", StringComparison.OrdinalIgnoreCase);

        public Task<string?> ResolveSecretAsync(string secretKey, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(Resolve(secretKey));

        public Task<string> ResolveConnectionStringAsync(string connectionStringTemplate, CancellationToken cancellationToken = default)
            => Task.FromResult(Resolve(connectionStringTemplate));

        public string[] GetSupportedProviders() => ["env"];

        private string Resolve(string secretKey)
        {
            ResolveCalls++;
            if (Fail)
            {
                throw new InvalidOperationException("resolver unavailable");
            }

            return Value;
        }
    }

    private sealed class CollectingLogger : ILogger<SecretProvider>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (exception is not null)
            {
                Messages.Add(exception.ToString());
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}

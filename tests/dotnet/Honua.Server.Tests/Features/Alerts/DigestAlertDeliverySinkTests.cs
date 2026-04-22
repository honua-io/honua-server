// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Server.Features.Alerts;
using Honua.Server.Features.Infrastructure.Events;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class DigestAlertDeliverySinkTests
{
    [UnitTest]
    public async Task FlushAsync_WithSuccessfulBatch_MarksDispatchItemsDelivered()
    {
        var dispatchStore = Substitute.For<IAlertDispatchStore>();
        var eventStore = Substitute.For<IAlertEventStore>();
        var dispatchItems = new[]
        {
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.Digest) with { DispatchId = 11, EventId = 101 },
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.Digest) with { DispatchId = 12, EventId = 102 }
        };

        dispatchStore.ClaimPendingDigestAsync(2, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(dispatchItems);
        eventStore.GetAsync(101, Arg.Any<CancellationToken>())
            .Returns(AlertTestFixtures.CreateAlertEvent(dedupeKey: "evt-101"));
        eventStore.GetAsync(102, Arg.Any<CancellationToken>())
            .Returns(AlertTestFixtures.CreateAlertEvent(dedupeKey: "evt-102"));

        var handler = new CapturingHandler(HttpStatusCode.OK);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("alerts-digest").Returns(new HttpClient(handler));

        using var provider = CreateServiceProvider(dispatchStore, eventStore);
        var service = new DigestFlushBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            httpClientFactory,
            Options.Create(CreateDigestOptions()),
            NullLogger<DigestFlushBackgroundService>.Instance);

        await service.FlushAsync(CancellationToken.None);

        await dispatchStore.Received(1).MarkDeliveredAsync(11, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await dispatchStore.Received(1).MarkDeliveredAsync(12, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await dispatchStore.DidNotReceiveWithAnyArgs().MarkFailedAsync(default, default, default, default, default, default);
        handler.RequestBody.Should().Contain("evt-101");
        handler.RequestBody.Should().Contain("evt-102");
        handler.EventTimestampHeader.Should().NotBeNullOrWhiteSpace();
        var expectedSignature = "sha256=" + WebhookDeliveryHelper.ComputeSignature("digest-secret", handler.EventTimestampHeader!, handler.RequestBody);
        handler.SignatureHeader.Should().Be(expectedSignature);
        handler.DigestCountHeader.Should().Be(2.ToString(CultureInfo.InvariantCulture));
    }

    [UnitTest]
    public async Task FlushAsync_WithServerError_MarksDispatchItemsFailedForRetry()
    {
        var dispatchStore = Substitute.For<IAlertDispatchStore>();
        var eventStore = Substitute.For<IAlertEventStore>();
        var dispatchItem = AlertTestFixtures.CreateDispatchItem(AlertChannelType.Digest) with
        {
            DispatchId = 21,
            EventId = 201,
            MaxAttempts = 4
        };

        dispatchStore.ClaimPendingDigestAsync(2, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([dispatchItem]);
        eventStore.GetAsync(201, Arg.Any<CancellationToken>())
            .Returns(AlertTestFixtures.CreateAlertEvent(dedupeKey: "evt-201"));

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("alerts-digest")
            .Returns(new HttpClient(new CapturingHandler(HttpStatusCode.InternalServerError)));

        using var provider = CreateServiceProvider(dispatchStore, eventStore);
        var service = new DigestFlushBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            httpClientFactory,
            Options.Create(CreateDigestOptions()),
            NullLogger<DigestFlushBackgroundService>.Instance);

        await service.FlushAsync(CancellationToken.None);

        await dispatchStore.Received(1).MarkFailedAsync(
            21,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<DateTimeOffset>(),
            false,
            Arg.Is<string?>(value => value != null && value.Contains("500", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await dispatchStore.DidNotReceiveWithAnyArgs().MarkDeliveredAsync(default, default, default);
    }

    private static AlertOptions CreateDigestOptions() =>
        new()
        {
            Dispatch = new AlertDispatchOptions
            {
                Digest = new DigestAlertOptions
                {
                    WebhookUrl = "https://example.com/digest",
                    WebhookSecret = "digest-secret",
                    MaxBatchSize = 2
                }
            }
        };

    private static ServiceProvider CreateServiceProvider(
        IAlertDispatchStore dispatchStore,
        IAlertEventStore eventStore)
    {
        var services = new ServiceCollection();
        services.AddSingleton(dispatchStore);
        services.AddSingleton(eventStore);
        return services.BuildServiceProvider();
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        public CapturingHandler(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        public string RequestBody { get; private set; } = string.Empty;

        public string? EventTimestampHeader { get; private set; }

        public string? SignatureHeader { get; private set; }

        public string? DigestCountHeader { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            EventTimestampHeader = Assert.Single(request.Headers.GetValues("X-Honua-Event-Timestamp"));
            SignatureHeader = Assert.Single(request.Headers.GetValues("X-Honua-Signature"));
            DigestCountHeader = Assert.Single(request.Headers.GetValues("X-Honua-Digest-Count"));

            return new HttpResponseMessage(_statusCode);
        }
    }
}

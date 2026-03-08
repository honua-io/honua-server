// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Reflection;
using Honua.Server.Features.Infrastructure.Events;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Events;

[Protocol(Protocols.TestQuality)]
public sealed class FeatureChangeWebhookDispatcherTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task FeatureChangeWebhookUrlValidation_RejectsPrivateAddressTargets()
    {
        var result = await FeatureChangeWebhookUrlValidation.ValidateAsync(
            "https://hooks.example.com/feature-change",
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.0.0.5") }));

        Assert.False(result.IsValid);
        Assert.Equal(FeatureChangeWebhookUrlValidation.DisallowedAddressMessage, result.ErrorMessage);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task DeliverWithRetryAsync_WhenWebhookUrlIsUnsafe_DoesNotSendRequest()
    {
        var store = new InMemoryFeatureChangeEventStore(
            Options.Create(new FeatureChangeEventOptions()),
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var handler = new CountingHandler();
        httpClientFactory.CreateClient("feature-change-webhook").Returns(new HttpClient(handler));

        var dispatcher = new FeatureChangeWebhookDispatcher(
            store,
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            httpClientFactory,
            Options.Create(new FeatureChangeWebhookOptions
            {
                Enabled = true,
                Url = "https://localhost/webhook",
                Secret = "super-secret",
                MaxAttempts = 1
            }),
            NullLogger<FeatureChangeWebhookDispatcher>.Instance);

        await InvokeDeliverWithRetryAsync(dispatcher, CreateEvent(), CancellationToken.None);

        httpClientFactory.DidNotReceive().CreateClient("feature-change-webhook");
        Assert.Equal(0, handler.SendCount);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void FeatureChangeWebhookOptionsValidator_WhenEnabledWithUnsafeUrl_FailsValidation()
    {
        var validator = new FeatureChangeWebhookOptionsValidator();

        var result = validator.Validate(
            name: null,
            new FeatureChangeWebhookOptions
            {
                Enabled = true,
                Url = "https://localhost/webhook",
                Secret = "secret",
                MaxAttempts = 1,
                RequestTimeoutSeconds = 5
            });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("unsafe", StringComparison.OrdinalIgnoreCase) ||
                                                   failure.Contains("private", StringComparison.OrdinalIgnoreCase));
    }

    private static FeatureChangeEvent CreateEvent()
        => new()
        {
            EventId = "evt-1",
            Cursor = 1,
            Timestamp = DateTimeOffset.UtcNow,
            ServiceId = "svc",
            LayerId = 42,
            ObjectId = 99,
            Operation = "update",
            Protocol = "FeatureServer",
            RequestId = "req-1"
        };

    private static async Task InvokeDeliverWithRetryAsync(
        FeatureChangeWebhookDispatcher dispatcher,
        FeatureChangeEvent featureEvent,
        CancellationToken cancellationToken)
    {
        var method = typeof(FeatureChangeWebhookDispatcher).GetMethod(
            "DeliverWithRetryAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var task = method!.Invoke(
            dispatcher,
            new object[] { featureEvent, new Uri("https://localhost/webhook"), cancellationToken }) as Task<bool>;
        Assert.NotNull(task);
        await task!;
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}

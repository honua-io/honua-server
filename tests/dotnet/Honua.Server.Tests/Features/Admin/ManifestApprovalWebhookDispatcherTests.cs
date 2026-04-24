// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Reflection;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

[Protocol(TestProtocols.TestQuality)]
public sealed class ManifestApprovalWebhookDispatcherTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ExecuteAsync_WhenDeliveryFails_RetriesSameEventUntilSuccess()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var handler = new RetryThenSuccessHandler(() => cts.Cancel());
        httpClientFactory.CreateClient("manifest-approval-webhook").Returns(new HttpClient(handler));

        var dispatcher = new ManifestApprovalWebhookDispatcher(
            httpClientFactory,
            Options.Create(new ManifestApprovalWebhookOptions
            {
                Enabled = true,
                Url = "https://example.com/manifest-approval",
                Secret = "super-secret",
                MaxAttempts = 1,
                InitialBackoffMs = 1,
                MaxBackoffMs = 1,
                RequestTimeoutSeconds = 5
            }),
            NullLogger<ManifestApprovalWebhookDispatcher>.Instance);

        dispatcher.Enqueue(new ManifestApprovalWebhookEvent
        {
            EventId = "evt-1",
            EventType = "manifest-approved",
            PendingId = Guid.NewGuid(),
            ManifestHash = "hash-1",
            Status = "approved",
            Actor = "reviewer",
            ResourceCount = 1,
            Timestamp = DateTimeOffset.UtcNow
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeExecuteAsync(dispatcher, cts.Token));
        Assert.Equal(2, handler.SendCount);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Enqueue_WhenQueueCapacityIsReached_ReturnsFalse()
    {
        var dispatcher = new ManifestApprovalWebhookDispatcher(
            Substitute.For<IHttpClientFactory>(),
            Options.Create(new ManifestApprovalWebhookOptions
            {
                Enabled = false
            }),
            NullLogger<ManifestApprovalWebhookDispatcher>.Instance);

        for (var i = 0; i < 1000; i++)
        {
            Assert.True(dispatcher.Enqueue(CreateEvent(i.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }

        Assert.False(dispatcher.Enqueue(CreateEvent("overflow")));
    }

    private static ManifestApprovalWebhookEvent CreateEvent(string suffix)
        => new()
        {
            EventId = $"evt-{suffix}",
            EventType = "manifest-approved",
            PendingId = Guid.NewGuid(),
            ManifestHash = $"hash-{suffix}",
            Status = "approved",
            Actor = "reviewer",
            ResourceCount = 1,
            Timestamp = DateTimeOffset.UtcNow
        };

    private static async Task InvokeExecuteAsync(
        ManifestApprovalWebhookDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var method = typeof(ManifestApprovalWebhookDispatcher).GetMethod(
            "ExecuteAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var task = method!.Invoke(dispatcher, new object[] { cancellationToken }) as Task;
        Assert.NotNull(task);
        await task!;
    }

    private sealed class RetryThenSuccessHandler(Action onSecondSend) : HttpMessageHandler
    {
        private readonly Action _onSecondSend = onSecondSend;

        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            if (SendCount >= 2)
            {
                _onSecondSend();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }
}

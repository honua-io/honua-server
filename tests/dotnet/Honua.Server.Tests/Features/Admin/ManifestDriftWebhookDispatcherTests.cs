// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Reflection;
using Honua.Core.Features.Metadata.Domain;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Admin;

[Protocol(TestProtocols.TestQuality)]
public sealed class ManifestDriftWebhookDispatcherTests
{
    private static readonly Uri WebhookUri = new("https://example.com/manifest-drift");

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task DeliverWithRetryAsync_WhenSameDriftDeliveredTwice_UsesStableEventHeaders()
    {
        var handler = new HeaderCaptureHandler();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("manifest-drift-webhook").Returns(new HttpClient(handler));

        var dispatcher = new ManifestDriftWebhookDispatcher(
            Substitute.For<IServiceScopeFactory>(),
            null,
            null,
            httpClientFactory,
            Options.Create(new ManifestDriftWebhookOptions
            {
                Enabled = true,
                Url = WebhookUri.AbsoluteUri,
                Secret = "super-secret",
                MaxAttempts = 1,
                RequestTimeoutSeconds = 5
            }),
            NullLogger<ManifestDriftWebhookDispatcher>.Instance);

        var report = new ManifestDriftReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            BaselineVersionId = "baseline-1",
            HasDrift = true,
            Resources =
            [
                new ManifestDriftRecord
                {
                    Identifier = new MetadataResourceIdentifier(MetadataResourceKinds.Layer, "default", "parks"),
                    DriftType = DriftTypes.SpecDrift,
                    DeclaredHash = "declared",
                    ActualHash = "actual"
                }
            ]
        };

        await InvokeDeliverWithRetryAsync(dispatcher, report, CancellationToken.None);
        await InvokeDeliverWithRetryAsync(dispatcher, report, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(handler.Requests[0].EventId, handler.Requests[1].EventId);
        Assert.Equal(handler.Requests[0].IdempotencyKey, handler.Requests[1].IdempotencyKey);
        Assert.StartsWith("manifest-drift-", handler.Requests[0].EventId, StringComparison.Ordinal);
        Assert.Equal(handler.Requests[0].EventId, handler.Requests[0].IdempotencyKey);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ExecuteAsync_WhenRedisStateLoadThrows_ContinuesUntilCancellation()
    {
        var database = Substitute.For<IDatabase>();
        database.StringGetAsync("manifest:drift:last-hash", Arg.Any<CommandFlags>())
            .Returns<Task<RedisValue>>(_ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "redis unavailable"));
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        var dispatcher = new ManifestDriftWebhookDispatcher(
            Substitute.For<IServiceScopeFactory>(),
            null,
            redis,
            Substitute.For<IHttpClientFactory>(),
            Options.Create(new ManifestDriftWebhookOptions
            {
                Enabled = false
            }),
            NullLogger<ManifestDriftWebhookDispatcher>.Instance);

        using var cts = new CancellationTokenSource();
        var executeTask = InvokeExecuteAsync(dispatcher, cts.Token);

        await Task.Delay(100);
        Assert.False(executeTask.IsCompleted);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executeTask);
    }

    private static async Task InvokeDeliverWithRetryAsync(
        ManifestDriftWebhookDispatcher dispatcher,
        ManifestDriftReport report,
        CancellationToken cancellationToken)
    {
        var method = typeof(ManifestDriftWebhookDispatcher).GetMethod(
            "DeliverWithRetryAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var task = method!.Invoke(
            dispatcher,
            new object[] { report, WebhookUri, cancellationToken }) as Task<bool>;
        Assert.NotNull(task);
        var delivered = await task!;
        Assert.True(delivered);
    }

    private static async Task InvokeExecuteAsync(
        ManifestDriftWebhookDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var method = typeof(ManifestDriftWebhookDispatcher).GetMethod(
            "ExecuteAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var task = method!.Invoke(
            dispatcher,
            new object[] { cancellationToken }) as Task;
        Assert.NotNull(task);
        await task!;
    }

    private sealed class HeaderCaptureHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.Headers.GetValues("X-Honua-Event-Id").Single(),
                request.Headers.GetValues("Idempotency-Key").Single()));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed record CapturedRequest(string EventId, string IdempotencyKey);
}

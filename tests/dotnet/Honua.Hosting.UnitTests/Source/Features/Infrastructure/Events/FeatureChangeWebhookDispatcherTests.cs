// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Reflection;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Infrastructure.Events;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Events;

[Protocol(TestProtocols.TestQuality)]
public sealed class FeatureChangeWebhookDispatcherTests
{
    private const string PublicIpLiteralWebhookUrl = "https://1.1.1.1/feature-change";
    private const string CursorName = FeatureChangeWebhookDispatcher.StreamName;

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
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var handler = new CountingHandler();
        using var httpClient = new HttpClient(handler);
        httpClientFactory.CreateClient("feature-change-webhook").Returns(httpClient);

        using var dispatcher = CreateDispatcher(new DispatcherStores(), httpClientFactory, "https://localhost/webhook");

        await InvokeDeliverWithRetryAsync(dispatcher, CreateEvent(1), CancellationToken.None);

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

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void AddValidatedHeader_WithControlCharacters_SanitizesHeaderValue()
    {
        using var request = new HttpRequestMessage();

        WebhookDeliveryHelper.AddValidatedHeader(request.Headers, "X-Honua-Event-Id", "evt-\r\n1");

        Assert.Equal("evt-1", Assert.Single(request.Headers.GetValues("X-Honua-Event-Id")));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void CreatePinnedDnsHttpMessageHandler_UsesSocketsHandlerWithPinnedConnectCallback()
    {
        var handler = WebhookDeliveryHelper.CreatePinnedDnsHttpMessageHandler();

        var socketsHandler = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(socketsHandler.AllowAutoRedirect);
        Assert.NotNull(socketsHandler.ConnectCallback);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ExecuteAsync_OnFirstStart_InitialisesCursorToHeadAndDeliversNothingOlder()
    {
        var stores = new DispatcherStores(new ListFeatureChangeEventStore(CreateEvent(1), CreateEvent(3)));
        var handler = new CountingHandler();
        using var httpClient = new HttpClient(handler);
        using var dispatcher = CreateDispatcher(stores, CreateHttpClientFactory(httpClient));

        using var cts = new CancellationTokenSource();
        var executeTask = InvokeExecuteAsync(dispatcher, cts.Token);

        await WaitForConditionAsync(
            () => stores.Events.QueriedCursors.Contains(3L),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(20));

        Assert.Equal(3L, stores.Cursors.Read(CursorName));
        Assert.DoesNotContain(0L, stores.Events.QueriedCursors);
        Assert.Equal(0, handler.SendCount);

        await CancelAndAwaitDispatcherShutdownAsync(cts, executeTask);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ExecuteAsync_WhenEventsAreDelivered_AdvancesStoredCursor()
    {
        var stores = new DispatcherStores(new ListFeatureChangeEventStore(CreateEvent(2), CreateEvent(5), CreateEvent(9)));
        stores.Cursors.Seed(CursorName, 2);
        var handler = new CountingHandler();
        using var httpClient = new HttpClient(handler);
        using var dispatcher = CreateDispatcher(stores, CreateHttpClientFactory(httpClient));

        using var cts = new CancellationTokenSource();
        var executeTask = InvokeExecuteAsync(dispatcher, cts.Token);

        await WaitForConditionAsync(
            () => stores.Cursors.Read(CursorName) == 9L,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(20));

        Assert.Equal(9L, stores.Cursors.Read(CursorName));
        Assert.Equal(2, handler.SendCount);

        await CancelAndAwaitDispatcherShutdownAsync(cts, executeTask);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ExecuteAsync_WhenAnotherOwnerHoldsLease_DeliversNothing()
    {
        var leases = new InMemoryLeaseStore();
        var stores = new DispatcherStores(new ListFeatureChangeEventStore(CreateEvent(1)), leases);
        stores.Cursors.Seed(CursorName, 0);
        leases.HoldForOtherOwner(CursorName);
        var handler = new CountingHandler();
        using var httpClient = new HttpClient(handler);
        using var dispatcher = CreateDispatcher(stores, CreateHttpClientFactory(httpClient));

        using var cts = new CancellationTokenSource();
        var executeTask = InvokeExecuteAsync(dispatcher, cts.Token);

        await WaitForConditionAsync(
            () => leases.AcquireAttempts >= 2,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(20));

        Assert.True(leases.AcquireAttempts >= 2);
        Assert.Empty(stores.Events.QueriedCursors);
        Assert.Equal(0, handler.SendCount);
        Assert.Equal(0L, stores.Cursors.Read(CursorName));

        await CancelAndAwaitDispatcherShutdownAsync(cts, executeTask);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ExecuteAsync_WhenLeaseIsLostDuringDelivery_CancelsInFlightWebhookRequest()
    {
        var leases = new InMemoryLeaseStore();
        var stores = new DispatcherStores(new ListFeatureChangeEventStore(CreateEvent(1)), leases);
        stores.Cursors.Seed(CursorName, 0);
        var handler = new CancellationAwareHandler();
        using var httpClient = new HttpClient(handler);
        using var dispatcher = CreateDispatcher(stores, CreateHttpClientFactory(httpClient));

        using var cts = new CancellationTokenSource();
        var executeTask = InvokeExecuteAsync(dispatcher, cts.Token);

        await handler.WaitForRequestAsync().WaitAsync(TimeSpan.FromSeconds(3));
        leases.HoldForOtherOwner(CursorName);

        await handler.WaitForCancellationAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0L, stores.Cursors.Read(CursorName));

        await CancelAndAwaitDispatcherShutdownAsync(cts, executeTask);
    }

    private static FeatureChangeWebhookDispatcher CreateDispatcher(
        DispatcherStores stores,
        IHttpClientFactory httpClientFactory,
        string url = PublicIpLiteralWebhookUrl)
        => new(
            stores.Events,
            stores.Cursors,
            stores.Leases,
            httpClientFactory,
            Options.Create(new FeatureChangeWebhookOptions
            {
                Enabled = true,
                Url = url,
                Secret = "super-secret",
                MaxAttempts = 1,
                RequestTimeoutSeconds = 30
            }),
            NullLogger<FeatureChangeWebhookDispatcher>.Instance);

    private static IHttpClientFactory CreateHttpClientFactory(HttpClient httpClient)
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("feature-change-webhook").Returns(httpClient);
        return httpClientFactory;
    }

    private static FeatureChangeEvent CreateEvent(long cursor)
        => new()
        {
            EventId = $"evt-{cursor}",
            Cursor = cursor,
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

    private static async Task InvokeExecuteAsync(
        FeatureChangeWebhookDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var method = typeof(FeatureChangeWebhookDispatcher).GetMethod(
            "ExecuteAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var task = method!.Invoke(
            dispatcher,
            new object[] { cancellationToken }) as Task;
        Assert.NotNull(task);
        await task!;
    }

    private static async Task WaitForConditionAsync(
        Func<bool> condition,
        TimeSpan timeout,
        TimeSpan pollInterval)
    {
        var start = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - start >= timeout)
            {
                break;
            }

            await Task.Delay(pollInterval);
        }
    }

    private static async Task CancelAndAwaitDispatcherShutdownAsync(
        CancellationTokenSource cancellationTokenSource,
        Task executeTask)
    {
        cancellationTokenSource.Cancel();
        try
        {
            await executeTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
        {
            // ExecuteAsync either observes cancellation at an await or exits at the loop guard.
        }
    }

    private sealed record DispatcherStores(ListFeatureChangeEventStore Events, InMemoryLeaseStore? Leases = null)
    {
        public DispatcherStores()
            : this(new ListFeatureChangeEventStore())
        {
        }

        public InMemoryStreamCursorStore Cursors { get; } = new();
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _sendCount;

        public int SendCount => Volatile.Read(ref _sendCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sendCount);
            // Ownership transfers to the HttpClient pipeline/caller, which disposes it; not this handler's responsibility.
            return Task.FromResult<HttpResponseMessage>(new Honua.TestKit.CallerOwnedHttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class CancellationAwareHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _requestCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForRequestAsync() => _requestStarted.Task;

        public Task WaitForCancellationAsync() => _requestCancelled.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requestStarted.TrySetResult();

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            catch (OperationCanceledException)
            {
                _requestCancelled.TrySetResult();
                throw;
            }
        }
    }

    private sealed class ListFeatureChangeEventStore(params FeatureChangeEvent[] events) : IFeatureChangeEventStore
    {
        private readonly ConcurrentQueue<long> _queriedCursors = new();

        public IReadOnlyCollection<long> QueriedCursors => _queriedCursors.ToArray();

        public Task<long> GetCurrentCursorAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(events.Length == 0 ? 0L : events.Max(featureEvent => featureEvent.Cursor));

        public Task<IReadOnlyList<FeatureChangeEvent>> QueryAsync(
            long? cursor,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int limit,
            CancellationToken cancellationToken = default)
        {
            var after = cursor ?? 0;
            _queriedCursors.Enqueue(after);
            return Task.FromResult<IReadOnlyList<FeatureChangeEvent>>(
                events.Where(featureEvent => featureEvent.Cursor > after)
                    .OrderBy(featureEvent => featureEvent.Cursor)
                    .Take(limit)
                    .ToArray());
        }
    }

    private sealed class InMemoryStreamCursorStore : IStreamCursorStore
    {
        private readonly ConcurrentDictionary<string, long> _positions = new(StringComparer.Ordinal);

        public long? Read(string name) => _positions.TryGetValue(name, out var position) ? position : null;

        public void Seed(string name, long position) => _positions[name] = position;

        public Task<long?> ReadAsync(string name, CancellationToken ct) => Task.FromResult(Read(name));

        public Task WriteAsync(string name, long position, CancellationToken ct)
        {
            _positions.AddOrUpdate(name, position, (_, stored) => Math.Max(stored, position));
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryLeaseStore : ILeaseStore
    {
        private readonly ConcurrentDictionary<string, string> _owners = new(StringComparer.Ordinal);
        private int _acquireAttempts;

        public int AcquireAttempts => Volatile.Read(ref _acquireAttempts);

        public void HoldForOtherOwner(string leaseKey) => _owners[leaseKey] = "other-owner";

        public Task<bool> TryAcquireAsync(LeaseClaim claim, TimeSpan duration, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _acquireAttempts);
            return Task.FromResult(_owners.TryAdd(claim.LeaseKey, claim.OwnerId));
        }

        public Task<bool> RenewAsync(LeaseClaim claim, TimeSpan duration, CancellationToken cancellationToken = default)
            => Task.FromResult(_owners.TryGetValue(claim.LeaseKey, out var owner) && owner == claim.OwnerId);

        public Task ReleaseAsync(LeaseClaim claim, CancellationToken cancellationToken = default)
        {
            _owners.TryRemove(new KeyValuePair<string, string>(claim.LeaseKey, claim.OwnerId));
            return Task.CompletedTask;
        }
    }
}

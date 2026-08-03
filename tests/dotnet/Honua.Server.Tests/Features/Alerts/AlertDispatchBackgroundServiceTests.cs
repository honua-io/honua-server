// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.Alerts;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Honua.Server.Tests.Infrastructure.Telemetry;

namespace Honua.Server.Tests.Features.Alerts;

/// <summary>
/// Behavioural tests for the alert dispatcher's suppression (#A11b-2) and per-channel circuit
/// breaking (#A11b-3): a suppressed event must be recorded but NOT delivered while suppressed, and a
/// channel whose circuit breaker is open must defer rather than drive delivery to the dead-letter path.
/// </summary>
public sealed class AlertDispatchBackgroundServiceTests
{
    [UnitTest]
    public async Task Dispatcher_SuppressedEvent_IsDeferredNotDelivered_WhileUnsuppressedEventDelivers()
    {
        const long suppressedEventId = 100;
        const long suppressedDispatchId = 1000;
        const long normalEventId = 200;
        const long normalDispatchId = 2000;

        var dispatchStore = Substitute.For<IAlertDispatchStore>();
        var eventStore = Substitute.For<IAlertEventStore>();
        var lifecycleStore = Substitute.For<IAlertLifecycleStore>();

        var batch = new[]
        {
            DispatchItem(suppressedDispatchId, suppressedEventId),
            DispatchItem(normalDispatchId, normalEventId),
        };
        ReturnBatchOnce(dispatchStore, batch);

        eventStore.GetAsync(suppressedEventId, Arg.Any<CancellationToken>()).Returns(Envelope("evt-suppressed"));
        eventStore.GetAsync(normalEventId, Arg.Any<CancellationToken>()).Returns(Envelope("evt-normal"));

        // The suppressed event carries an open suppression window; the normal event has no lifecycle row.
        lifecycleStore.GetAsync(suppressedEventId, Arg.Any<CancellationToken>()).Returns(new AlertEventLifecycle
        {
            EventId = suppressedEventId,
            Status = AlertLifecycleStatus.Suppressed,
            SuppressedUntil = DateTimeOffset.UtcNow.AddHours(1),
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        lifecycleStore.GetAsync(normalEventId, Arg.Any<CancellationToken>()).Returns((AlertEventLifecycle?)null);

        var sink = new RecordingSink();
        await RunDispatcherAsync(dispatchStore, eventStore, lifecycleStore, sink, () => !sink.Delivered.IsEmpty);

        sink.Delivered.Should().ContainSingle().Which.Should().Be(normalEventId, "only the unsuppressed event delivers");
        sink.Delivered.Should().NotContain(suppressedEventId, "a suppressed event must not deliver while suppressed");

        // Suppressed dispatch is deferred (rescheduled), never delivered or dead-lettered — the event stays recorded.
        await dispatchStore.Received().RescheduleAsync(suppressedDispatchId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await dispatchStore.DidNotReceive().MarkDeliveredAsync(suppressedDispatchId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await dispatchStore.Received().MarkDeliveredAsync(normalDispatchId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task Dispatcher_WhenChannelCircuitOpen_DefersInsteadOfDelivering()
    {
        const long eventId = 300;
        const long dispatchId = 3000;

        var dispatchStore = Substitute.For<IAlertDispatchStore>();
        var eventStore = Substitute.For<IAlertEventStore>();
        var lifecycleStore = Substitute.For<IAlertLifecycleStore>();

        ReturnBatchOnce(dispatchStore, new[] { DispatchItem(dispatchId, eventId) });
        eventStore.GetAsync(eventId, Arg.Any<CancellationToken>()).Returns(Envelope("evt"));
        lifecycleStore.GetAsync(eventId, Arg.Any<CancellationToken>()).Returns((AlertEventLifecycle?)null);

        var sink = new RecordingSink();
        // Trip the breaker open for the webhook channel before the dispatcher runs.
        var options = BuildOptions(circuitThreshold: 1);
        var breaker = new AlertChannelCircuitBreaker(options);
        breaker.RecordDeadLetter(AlertChannelType.Webhook, DateTimeOffset.UtcNow);

        await RunDispatcherAsync(
            dispatchStore, eventStore, lifecycleStore, sink,
            stopWhen: () => false,
            options: options,
            breaker: breaker,
            settleDelayMs: 400);

        sink.Delivered.Should().BeEmpty("an open channel defers delivery rather than driving it to dead-letter");
        await dispatchStore.Received().RescheduleAsync(dispatchId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await dispatchStore.DidNotReceive().MarkFailedAsync(
            dispatchId, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task Dispatcher_WhenChannelEditionIsInactive_ReschedulesWithoutConsumingRetryBudget()
    {
        const long eventId = 400;
        const long dispatchId = 4000;

        var dispatchStore = Substitute.For<IAlertDispatchStore>();
        var eventStore = Substitute.For<IAlertEventStore>();
        var lifecycleStore = Substitute.For<IAlertLifecycleStore>();
        var editionPolicy = Substitute.For<IAlertEditionPolicy>();
        editionPolicy.IsChannelAllowed(AlertChannelType.Webhook).Returns(false);

        ReturnBatchOnce(dispatchStore, new[] { DispatchItem(dispatchId, eventId) });
        var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decisionStartedAt = DateTimeOffset.UtcNow;
        DateTimeOffset? retryAt = null;
        dispatchStore
            .RescheduleAsync(dispatchId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                retryAt = call.ArgAt<DateTimeOffset>(1);
                deferred.TrySetResult();
                return Task.CompletedTask;
            });

        var sink = new RecordingSink();
        await RunDispatcherAsync(
            dispatchStore,
            eventStore,
            lifecycleStore,
            sink,
            stopWhen: () => deferred.Task.IsCompleted,
            editionPolicy: editionPolicy,
            settleDelayMs: 50);

        sink.Delivered.Should().BeEmpty("an inactive channel entitlement defers delivery until it can become active again");
        await dispatchStore.Received(1).RescheduleAsync(
            dispatchId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        retryAt.Should().NotBeNull();
        retryAt!.Value.Should().BeCloseTo(
            decisionStartedAt.AddMinutes(5),
            TimeSpan.FromSeconds(5),
            "the queue needs a bounded delay rather than an immediate entitlement spin");
        await dispatchStore.DidNotReceive().MarkFailedAsync(
            dispatchId, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await dispatchStore.DidNotReceive().MarkDeliveredAsync(
            dispatchId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await eventStore.DidNotReceive().GetAsync(eventId, Arg.Any<CancellationToken>());
    }

    private static void ReturnBatchOnce(IAlertDispatchStore dispatchStore, IReadOnlyList<AlertDispatchItem> batch)
    {
        var served = 0;
        dispatchStore.ClaimPendingAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(_ => Interlocked.Exchange(ref served, 1) == 0
                ? batch
                : Array.Empty<AlertDispatchItem>());
        dispatchStore.GetBacklogAsync(Arg.Any<CancellationToken>())
            .Returns(new AlertDispatchBacklog { PendingCount = 0, DeadLetteredCount = 0 });
    }

    private static async Task RunDispatcherAsync(
        IAlertDispatchStore dispatchStore,
        IAlertEventStore eventStore,
        IAlertLifecycleStore lifecycleStore,
        IAlertDeliverySink sink,
        Func<bool> stopWhen,
        IAlertEditionPolicy? editionPolicy = null,
        IOptions<AlertOptions>? options = null,
        AlertChannelCircuitBreaker? breaker = null,
        int settleDelayMs = 2000)
    {
        options ??= BuildOptions();
        breaker ??= new AlertChannelCircuitBreaker(options);

        var services = new ServiceCollection();
        services.AddScoped(_ => dispatchStore);
        services.AddScoped(_ => eventStore);
        services.AddScoped(_ => lifecycleStore);
        if (editionPolicy is not null)
        {
            services.AddScoped<IAlertEditionPolicy>(_ => editionPolicy);
        }
        await using var provider = services.BuildServiceProvider();

        var dispatcher = new AlertDispatchBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new[] { sink },
            new AlertNotificationRateLimiter(),
            breaker,
            options,
            TestTelemetry.CreateAlertPipelineMetrics(),
            NullLogger<AlertDispatchBackgroundService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await dispatcher.StartAsync(cts.Token);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!stopWhen() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25, cts.Token);
        }

        // Give the loop a moment to settle the (non-)delivery decision when there is nothing to wait on.
        await Task.Delay(settleDelayMs, cts.Token);
        await dispatcher.StopAsync(CancellationToken.None);
    }

    private static IOptions<AlertOptions> BuildOptions(int circuitThreshold = 5)
        => Options.Create(new AlertOptions
        {
            Enabled = true,
            Dispatch = new AlertDispatchOptions
            {
                IdleDelay = TimeSpan.FromMilliseconds(50),
                CircuitBreakerThreshold = circuitThreshold,
                CircuitBreakerCooldown = TimeSpan.FromMinutes(5),
            },
        });

    private static AlertDispatchItem DispatchItem(long dispatchId, long eventId)
        => new()
        {
            DispatchId = dispatchId,
            EventId = eventId,
            ChannelType = AlertChannelType.Webhook,
            Status = AlertDispatchStatus.Pending,
            Attempts = 0,
            MaxAttempts = 5,
            NextAttemptAt = DateTimeOffset.UtcNow,
        };

    private static AlertEventEnvelope Envelope(string dedupeKey)
        => new()
        {
            DedupeKey = dedupeKey,
            RuleId = 1,
            ServiceId = "svc",
            LayerId = 1,
            ObjectId = 1,
            TriggerType = AlertTriggerType.Threshold,
            Generation = 1,
            Severity = AlertSeverity.Warning,
            OccurredAt = DateTimeOffset.UtcNow,
        };

    private sealed class RecordingSink : IAlertDeliverySink
    {
        public ConcurrentBag<long> Delivered { get; } = new();

        public AlertChannelType ChannelType => AlertChannelType.Webhook;

        public Task<AlertDeliveryResult> DeliverAsync(
            AlertDispatchItem dispatchItem,
            AlertEventEnvelope alertEvent,
            CancellationToken cancellationToken = default)
        {
            Delivered.Add(dispatchItem.EventId);
            return Task.FromResult(new AlertDeliveryResult { Succeeded = true, Retryable = false });
        }
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Mobile.FieldCollection.Abstractions;
using Honua.Core.Features.Mobile.FieldCollection.Domain;
using Honua.Server.Features.Mobile.FieldCollection.Automations;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Mobile.FieldCollection;

public sealed class FieldCollectionAutomationDispatchReloadTests
{
    private static readonly TimeSpan DeliveryWait = TimeSpan.FromSeconds(5);

    [UnitTest]
    public async Task Dispatch_EnabledByReloadAfterStartup_DeliversQueuedInvocation()
    {
        var monitor = new MutableOptionsMonitor(new FieldCollectionAutomationOptions { Enabled = false, MaxAttempts = 1 });
        var dispatcher = new ChannelFieldCollectionActionDispatcher(
            Options.Create(monitor.CurrentValue),
            NullLogger<ChannelFieldCollectionActionDispatcher>.Instance);
        var handler = new RecordingHandler();
        using var service = new FieldCollectionAutomationDispatchBackgroundService(
            dispatcher, [handler], monitor, NullLogger<FieldCollectionAutomationDispatchBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        monitor.CurrentValue = new FieldCollectionAutomationOptions { Enabled = true, MaxAttempts = 1 };
        await dispatcher.EnqueueAsync(CreateInvocation("inv-reload"));

        var delivered = await handler.Delivered.Task.WaitAsync(DeliveryWait);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal("inv-reload", delivered);
    }

    [UnitTest]
    public async Task Process_WhenDisabledByReload_DropsInvocationWithoutDelivery()
    {
        var monitor = new MutableOptionsMonitor(new FieldCollectionAutomationOptions { Enabled = true, MaxAttempts = 1 });
        var dispatcher = new ChannelFieldCollectionActionDispatcher(
            Options.Create(monitor.CurrentValue),
            NullLogger<ChannelFieldCollectionActionDispatcher>.Instance);
        var handler = new RecordingHandler();
        using var service = new FieldCollectionAutomationDispatchBackgroundService(
            dispatcher, [handler], monitor, NullLogger<FieldCollectionAutomationDispatchBackgroundService>.Instance);

        monitor.CurrentValue = new FieldCollectionAutomationOptions { Enabled = false, MaxAttempts = 1 };
        await service.ProcessAsync(CreateInvocation("inv-off"), CancellationToken.None);

        Assert.Equal(0, handler.Calls);
    }

    [UnitTest]
    public async Task Process_UsesMaxAttemptsFromCurrentOptions()
    {
        var monitor = new MutableOptionsMonitor(new FieldCollectionAutomationOptions { Enabled = true, MaxAttempts = 4 });
        var dispatcher = new ChannelFieldCollectionActionDispatcher(
            Options.Create(monitor.CurrentValue),
            NullLogger<ChannelFieldCollectionActionDispatcher>.Instance);
        var handler = new RecordingHandler { Result = FieldCollectionActionResult.Failure("down", retryable: true) };
        using var service = new FieldCollectionAutomationDispatchBackgroundService(
            dispatcher, [handler], monitor, NullLogger<FieldCollectionAutomationDispatchBackgroundService>.Instance);

        monitor.CurrentValue = new FieldCollectionAutomationOptions { Enabled = true, MaxAttempts = 1 };
        await service.ProcessAsync(CreateInvocation("inv-once"), CancellationToken.None);

        Assert.Equal(1, handler.Calls);
    }

    private static FieldCollectionActionInvocation CreateInvocation(string invocationId)
        => new()
        {
            InvocationId = invocationId,
            Action = new FieldCollectionAutomationAction
            {
                Id = "webhook-1",
                DisplayName = "webhook-1",
                ActionType = FieldCollectionAutomationActionType.Webhook,
                Operations = ImmutableArray<FieldCollectionChangeOperation>.Empty,
                Configuration = ImmutableDictionary<string, string>.Empty,
            },
            Event = new FieldCollectionAutomationEvent
            {
                ClientId = "device-1",
                ChangeId = "chg-1",
                FeatureId = "feat-1",
                LayerId = 7,
                Operation = FieldCollectionChangeOperation.Insert,
                Version = 1,
                Generation = 1,
                Timestamp = DateTimeOffset.UnixEpoch,
            },
        };

    private sealed class RecordingHandler : IFieldCollectionActionHandler
    {
        private int _calls;

        public TaskCompletionSource<string> Delivered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FieldCollectionActionResult Result { get; init; } = FieldCollectionActionResult.Success();

        public int Calls => Volatile.Read(ref _calls);

        public FieldCollectionAutomationActionType ActionType => FieldCollectionAutomationActionType.Webhook;

        public Task<FieldCollectionActionResult> ExecuteAsync(
            FieldCollectionActionInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            Delivered.TrySetResult(invocation.InvocationId);
            return Task.FromResult(Result);
        }
    }

    private sealed class MutableOptionsMonitor(FieldCollectionAutomationOptions initial)
        : IOptionsMonitor<FieldCollectionAutomationOptions>
    {
        private FieldCollectionAutomationOptions _current = initial;

        public FieldCollectionAutomationOptions CurrentValue
        {
            get => Volatile.Read(ref _current);
            set => Volatile.Write(ref _current, value);
        }

        public FieldCollectionAutomationOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<FieldCollectionAutomationOptions, string?> listener) => null;
    }
}

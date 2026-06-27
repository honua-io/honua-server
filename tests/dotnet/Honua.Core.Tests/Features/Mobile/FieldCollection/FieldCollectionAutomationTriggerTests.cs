// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Mobile.FieldCollection;
using Honua.Core.Features.Mobile.FieldCollection.Abstractions;
using Honua.Core.Features.Mobile.FieldCollection.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Mobile.FieldCollection;

public sealed class FieldCollectionAutomationTriggerTests
{
    private static FieldCollectionAutomationEvent CreateEvent(int layerId = 100)
        => new()
        {
            ClientId = "device-1",
            ChangeId = "chg-1",
            FeatureId = "feat-1",
            LayerId = layerId,
            Operation = FieldCollectionChangeOperation.Insert,
            Version = 1,
            Generation = 1,
            Timestamp = DateTimeOffset.UnixEpoch,
        };

    private static FieldCollectionAutomationAction CreateAction(string id, bool enabled = true)
        => new()
        {
            Id = id,
            DisplayName = id,
            ActionType = FieldCollectionAutomationActionType.Webhook,
            Enabled = enabled,
            Operations = ImmutableArray<FieldCollectionChangeOperation>.Empty,
        };

    [UnitTest]
    public async Task OnChangeApplied_EnqueuesEveryMatchedAction()
    {
        var store = new FakeStore(CreateAction("a1"), CreateAction("a2"));
        var dispatcher = new RecordingDispatcher();
        var trigger = new FieldCollectionAutomationTrigger(
            store,
            dispatcher,
            NullLogger<FieldCollectionAutomationTrigger>.Instance);

        await trigger.OnChangeAppliedAsync(CreateEvent());

        Assert.Equal(2, dispatcher.Enqueued.Count);
        Assert.Equal("a1", dispatcher.Enqueued[0].Action.Id);
        Assert.Equal("a2", dispatcher.Enqueued[1].Action.Id);
        Assert.Equal(100, store.RequestedLayerId);
    }

    [UnitTest]
    public async Task OnChangeApplied_NoEnabledActions_EnqueuesNothing()
    {
        var store = new FakeStore(CreateAction("disabled", enabled: false));
        var dispatcher = new RecordingDispatcher();
        var trigger = new FieldCollectionAutomationTrigger(
            store,
            dispatcher,
            NullLogger<FieldCollectionAutomationTrigger>.Instance);

        await trigger.OnChangeAppliedAsync(CreateEvent());

        Assert.Empty(dispatcher.Enqueued);
    }

    [UnitTest]
    public async Task OnChangeApplied_StoreThrows_DoesNotPropagate()
    {
        var store = new ThrowingStore();
        var dispatcher = new RecordingDispatcher();
        var trigger = new FieldCollectionAutomationTrigger(
            store,
            dispatcher,
            NullLogger<FieldCollectionAutomationTrigger>.Instance);

        // Must not throw — automation is best-effort and never fails the push.
        await trigger.OnChangeAppliedAsync(CreateEvent());

        Assert.Empty(dispatcher.Enqueued);
    }

    [UnitTest]
    public async Task OnChangeApplied_DispatcherThrowsForOne_StillEnqueuesOthers()
    {
        var store = new FakeStore(CreateAction("a1"), CreateAction("a2"));
        var dispatcher = new RecordingDispatcher { ThrowForActionId = "a1" };
        var trigger = new FieldCollectionAutomationTrigger(
            store,
            dispatcher,
            NullLogger<FieldCollectionAutomationTrigger>.Instance);

        await trigger.OnChangeAppliedAsync(CreateEvent());

        Assert.Single(dispatcher.Enqueued);
        Assert.Equal("a2", dispatcher.Enqueued[0].Action.Id);
    }

    private sealed class FakeStore : IFieldCollectionAutomationStore
    {
        private readonly FieldCollectionAutomationAction[] _actions;

        public FakeStore(params FieldCollectionAutomationAction[] actions) => _actions = actions;

        public int RequestedLayerId { get; private set; }

        public Task<IReadOnlyList<FieldCollectionAutomationAction>> GetEnabledActionsAsync(
            int layerId,
            CancellationToken cancellationToken = default)
        {
            RequestedLayerId = layerId;
            return Task.FromResult<IReadOnlyList<FieldCollectionAutomationAction>>(_actions);
        }
    }

    private sealed class ThrowingStore : IFieldCollectionAutomationStore
    {
        public Task<IReadOnlyList<FieldCollectionAutomationAction>> GetEnabledActionsAsync(
            int layerId,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("store unavailable");
    }

    private sealed class RecordingDispatcher : IFieldCollectionActionDispatcher
    {
        public List<FieldCollectionActionInvocation> Enqueued { get; } = new();

        public string? ThrowForActionId { get; init; }

        public ValueTask EnqueueAsync(
            FieldCollectionActionInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            if (ThrowForActionId is not null && invocation.Action.Id == ThrowForActionId)
            {
                throw new InvalidOperationException("queue full");
            }

            Enqueued.Add(invocation);
            return ValueTask.CompletedTask;
        }
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Mobile.FieldCollection;
using Honua.Core.Features.Mobile.FieldCollection.Domain;
using Honua.Server.Features.Mobile.FieldCollection.Automations;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Mobile.FieldCollection;

/// <summary>
/// Dispatch kill-switch and bounded-queue behavior for FieldCollection automation (#6125).
/// </summary>
public sealed class FieldCollectionAutomationDispatchDisabledTests
{
    [UnitTest]
    public async Task OnChangeApplied_WhenDispatchDisabled_SecondCallDoesNotBlockOrEnqueue()
    {
        var options = CreateOptions(enabled: false, queueCapacity: 1);
        var dispatcher = CreateDispatcher(options);
        var trigger = CreateTrigger(options, dispatcher);
        var automationEvent = CreateEvent();

        await trigger.OnChangeAppliedAsync(automationEvent).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        await trigger.OnChangeAppliedAsync(automationEvent).AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(dispatcher.Reader.TryRead(out _));
    }

    [UnitTest]
    public async Task OnChangeApplied_WhenDispatchEnabled_EnqueuesMatchingAction()
    {
        var options = CreateOptions(enabled: true, queueCapacity: 4);
        var dispatcher = CreateDispatcher(options);
        var trigger = CreateTrigger(options, dispatcher);

        await trigger.OnChangeAppliedAsync(CreateEvent()).AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(dispatcher.Reader.TryRead(out var invocation));
        Assert.Equal("webhook-1", invocation.Action.Id);
        Assert.False(dispatcher.Reader.TryRead(out _));
    }

    [UnitTest]
    public async Task Enqueue_WhenQueueIsFull_DropsInsteadOfBlocking()
    {
        var options = CreateOptions(enabled: true, queueCapacity: 1);
        var dispatcher = CreateDispatcher(options);
        var trigger = CreateTrigger(options, dispatcher);
        var automationEvent = CreateEvent();

        await trigger.OnChangeAppliedAsync(automationEvent).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        await trigger.OnChangeAppliedAsync(automationEvent).AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(dispatcher.Reader.TryRead(out _));
        Assert.False(dispatcher.Reader.TryRead(out _));
    }

    private static FieldCollectionAutomationOptions CreateOptions(bool enabled, int queueCapacity)
        => new()
        {
            Enabled = enabled,
            QueueCapacity = queueCapacity,
            Actions = new List<FieldCollectionAutomationActionDefinition>
            {
                new()
                {
                    Id = "webhook-1",
                    DisplayName = "webhook-1",
                    ActionType = FieldCollectionAutomationActionType.Webhook,
                    Enabled = true,
                    Configuration = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["url"] = "https://hooks.example.com/field",
                        ["secret"] = "signing-secret",
                    },
                },
            },
        };

    private static ChannelFieldCollectionActionDispatcher CreateDispatcher(FieldCollectionAutomationOptions options)
        => new(Options.Create(options), NullLogger<ChannelFieldCollectionActionDispatcher>.Instance);

    private static FieldCollectionAutomationTrigger CreateTrigger(
        FieldCollectionAutomationOptions options,
        ChannelFieldCollectionActionDispatcher dispatcher)
        => new(
            new OptionsFieldCollectionAutomationStore(new FixedOptionsMonitor(options)),
            dispatcher,
            NullLogger<FieldCollectionAutomationTrigger>.Instance);

    private static FieldCollectionAutomationEvent CreateEvent()
        => new()
        {
            ClientId = "device-1",
            ChangeId = "chg-1",
            FeatureId = "feat-1",
            LayerId = 7,
            Operation = FieldCollectionChangeOperation.Insert,
            Version = 1,
            Generation = 1,
            Timestamp = DateTimeOffset.UnixEpoch,
        };

    private sealed class FixedOptionsMonitor(FieldCollectionAutomationOptions value) : IOptionsMonitor<FieldCollectionAutomationOptions>
    {
        public FieldCollectionAutomationOptions CurrentValue { get; } = value;

        public FieldCollectionAutomationOptions Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<FieldCollectionAutomationOptions, string?> listener) => EmptyDisposable.Instance;

        private sealed class EmptyDisposable : IDisposable
        {
            public static readonly EmptyDisposable Instance = new();

            public void Dispose()
            {
            }
        }
    }
}

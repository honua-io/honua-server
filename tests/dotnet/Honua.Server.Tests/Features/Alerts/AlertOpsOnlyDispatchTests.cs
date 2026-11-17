// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Alerts;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Server.Tests.Infrastructure.Telemetry;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Alerts;

// honua-server#8191: ops notifications are delivered without the Preview geofence pipeline.
public sealed class AlertOpsOnlyDispatchTests
{
    [UnitTest]
    public async Task Dispatcher_OpsOnWithoutGeofence_ClaimsOnlyOpsDispatches()
    {
        var store = NewStore();
        var options = BuildOptions(new AlertOptions { Enabled = false, Ops = OpsOn() });

        await RunUntilAsync(store, options, () => store.ReceivedCalls().Any(IsCall("ClaimPendingOpsAsync")));

        await store.DidNotReceiveWithAnyArgs().ClaimPendingAsync(default, default, default);
    }

    [UnitTest]
    public async Task Dispatcher_GeofenceOn_ClaimsEveryDispatch()
    {
        var store = NewStore();
        var options = BuildOptions(new AlertOptions { Enabled = true, Ops = OpsOn() });

        await RunUntilAsync(store, options, () => store.ReceivedCalls().Any(IsCall("ClaimPendingAsync")));

        await store.DidNotReceiveWithAnyArgs().ClaimPendingOpsAsync(default, default, default);
    }

    [UnitTest]
    public async Task Dispatcher_OpsAndGeofenceOff_StaysDormant()
    {
        var store = NewStore();
        var options = BuildOptions(new AlertOptions { Enabled = false });
        await using var provider = BuildProvider(store);
        using var dispatcher = NewDispatcher(provider, options);

        dispatcher.IsDispatcherEnabled.Should().BeFalse();
        await dispatcher.StartAsync(CancellationToken.None);
        await dispatcher.StopAsync(CancellationToken.None);

        dispatcher.IsDispatcherRunning.Should().BeFalse();
        store.ReceivedCalls().Should().BeEmpty();
    }

    private static AlertOpsOptions OpsOn() => new() { Enabled = true, Channels = ["aws_sns"] };

    private static Func<NSubstitute.Core.ICall, bool> IsCall(string name)
        => call => call.GetMethodInfo().Name == name;

    private static IAlertDispatchStore NewStore()
    {
        var store = Substitute.For<IAlertDispatchStore>();
        store.ClaimPendingAsync(default, default, default).ReturnsForAnyArgs(Array.Empty<AlertDispatchItem>());
        store.ClaimPendingOpsAsync(default, default, default).ReturnsForAnyArgs(Array.Empty<AlertDispatchItem>());
        store.GetBacklogAsync(default).ReturnsForAnyArgs(new AlertDispatchBacklog { PendingCount = 0, DeadLetteredCount = 0 });
        return store;
    }

    private static IOptions<AlertOptions> BuildOptions(AlertOptions options)
    {
        options.Dispatch = new AlertDispatchOptions
        {
            IdleDelay = TimeSpan.FromMilliseconds(10),
            InitialBackoff = TimeSpan.FromMilliseconds(10),
            BacklogRefreshInterval = TimeSpan.FromHours(1),
            DeliveredRetention = TimeSpan.Zero,
        };
        return Options.Create(options);
    }

    private static ServiceProvider BuildProvider(IAlertDispatchStore store)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(Substitute.For<IAlertEventStore>());
        services.AddSingleton(Substitute.For<IAlertLifecycleStore>());
        return services.BuildServiceProvider();
    }

    private static AlertDispatchBackgroundService NewDispatcher(ServiceProvider provider, IOptions<AlertOptions> options)
        => new(
            provider.GetRequiredService<IServiceScopeFactory>(), [], new AlertNotificationRateLimiter(),
            new AlertChannelCircuitBreaker(options), options, TestTelemetry.CreateAlertPipelineMetrics(),
            NullLogger<AlertDispatchBackgroundService>.Instance);

    private static async Task RunUntilAsync(IAlertDispatchStore store, IOptions<AlertOptions> options, Func<bool> condition)
    {
        await using var provider = BuildProvider(store);
        using var dispatcher = NewDispatcher(provider, options);
        dispatcher.IsDispatcherEnabled.Should().BeTrue();
        await dispatcher.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!condition())
            {
                DateTime.UtcNow.Should().BeBefore(deadline, "the dispatcher should poll within five seconds");
                await Task.Delay(10);
            }
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }
    }
}

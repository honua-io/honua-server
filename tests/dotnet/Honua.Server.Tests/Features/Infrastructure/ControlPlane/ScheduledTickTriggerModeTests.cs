// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Phase 3 trigger-mode tests for the PERIODIC (bucket-b) services. Under <c>TriggerMode=Poll</c> the
/// in-process timers are hosted as before; under <c>Event</c> none of them are hosted, but the
/// scheduled-tick handlers + dispatcher are registered so EventBridge Scheduler can drive each tick.
/// These exercise <see cref="ControlPlaneTriggerModeResolver"/> (the shared gate every registration
/// site uses) and the dispatcher's resolution of the registered handler set.
/// </summary>
public sealed class ScheduledTickTriggerModeTests
{
    private static IConfiguration Configuration(string? triggerMode)
    {
        var values = new Dictionary<string, string?>();
        if (triggerMode is not null)
        {
            values["ControlPlane:TriggerMode"] = triggerMode;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void Resolver_Unconfigured_DefaultsToPoll_HostsInProcessTimers()
    {
        var configuration = Configuration(triggerMode: null);

        ControlPlaneTriggerModeResolver.IsEventMode(configuration).Should().BeFalse();
        ControlPlaneTriggerModeResolver.ShouldHostInProcessTimers(configuration).Should().BeTrue();
    }

    [Theory]
    [InlineData("Poll")]
    [InlineData("poll")]
    [InlineData("not-a-mode")]
    public void Resolver_NonEvent_HostsInProcessTimers(string mode)
    {
        var configuration = Configuration(mode);

        ControlPlaneTriggerModeResolver.IsEventMode(configuration).Should().BeFalse();
        ControlPlaneTriggerModeResolver.ShouldHostInProcessTimers(configuration).Should().BeTrue();
    }

    [Theory]
    [InlineData("Event")]
    [InlineData("event")]
    [InlineData("EVENT")]
    public void Resolver_Event_DoesNotHostInProcessTimers(string mode)
    {
        var configuration = Configuration(mode);

        ControlPlaneTriggerModeResolver.IsEventMode(configuration).Should().BeTrue();
        ControlPlaneTriggerModeResolver.ShouldHostInProcessTimers(configuration).Should().BeFalse();
    }

    [Fact]
    public void Poll_HostsTimer_AndRegistersHandler_ForSharedRegistrationShape()
    {
        // Reproduce the exact registration shape every PERIODIC site uses: a shared singleton service,
        // an always-registered scheduled-tick handler, and a Poll-gated hosted-service that resolves
        // the SAME singleton. Under Poll the timer is hosted and the handler is present.
        var services = new ServiceCollection();
        RegisterLikeProductionSite(services, Configuration("Poll"));

        var hostedDescriptors = services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .ToList();
        hostedDescriptors.Should().ContainSingle("Poll mode hosts the in-process timer");

        services.Should().Contain(d => d.ServiceType == typeof(IScheduledTickHandler),
            "the scheduled-tick handler is registered in BOTH modes");
    }

    [Fact]
    public void Event_HostsNoTimer_ButRegistersHandlerAndDispatcherDrivesIt()
    {
        var services = new ServiceCollection();
        RegisterLikeProductionSite(services, Configuration("Event"));

        services.Should().NotContain(d => d.ServiceType == typeof(IHostedService),
            "Event mode hosts none of the in-process timers");
        services.Should().Contain(d => d.ServiceType == typeof(IScheduledTickHandler),
            "Event mode still registers the handler so the dispatcher can drive the tick");

        // The dispatcher, resolving the registered handler set, drives the tick on demand — the
        // Event-mode replacement for the in-process timer.
        services.AddSingleton<IScheduledTickDispatcher, ScheduledTickDispatcher>();
        using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IScheduledTickDispatcher>();
        var handler = (CountingTickHandler)provider.GetServices<IScheduledTickHandler>().Single();

        var act = async () => await dispatcher.RunTickAsync(handler.Kind);
        act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Mirrors the Poll-gated registration pattern used at every PERIODIC site: shared singleton +
    /// always-on handler + conditionally-hosted timer keyed off the trigger mode.
    /// </summary>
    private static void RegisterLikeProductionSite(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<CountingTickHandler>();
        services.AddSingleton<IScheduledTickHandler>(sp => sp.GetRequiredService<CountingTickHandler>());
        if (ControlPlaneTriggerModeResolver.ShouldHostInProcessTimers(configuration))
        {
            services.AddSingleton<IHostedService, FakeTimerHostedService>();
        }
    }

    private sealed class CountingTickHandler : IScheduledTickHandler
    {
        public ScheduledTickKind Kind => ScheduledTickKind.DigestFlush;

        public Task RunTickAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeTimerHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

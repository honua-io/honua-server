// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Plugins.Abstractions;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Honua.Plugins.Tests;

public sealed class PluginBackgroundServiceHostTests
{
    private static IServiceScopeFactory ScopeFactory(IAuditLog? auditLog = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => auditLog ?? new RecordingAuditLog());
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static PluginBackgroundServiceHost CreateHost(
        IEnumerable<IPluginBackgroundService> services,
        HonuaEdition edition = HonuaEdition.Enterprise,
        bool enabled = true,
        IAuditLog? auditLog = null)
        => new(
            services,
            new TestLicenseEntitlementService(edition),
            ScopeFactory(auditLog),
            Options.Create(new PluginOptions { Enabled = enabled }),
            NullLogger<PluginBackgroundServiceHost>.Instance);

    [Fact]
    public async Task StartsService_WhenEntitled()
    {
        var service = new SignalingService();
        var host = CreateHost([service]);

        await host.StartAsync(CancellationToken.None);
        (await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();

        await host.StopAsync(CancellationToken.None);
        service.SawCancellation.Should().BeTrue();
    }

    [Theory]
    [InlineData(HonuaEdition.Community)]
    [InlineData(HonuaEdition.Pro)]
    public async Task DoesNotStart_WhenNotEntitled(HonuaEdition edition)
    {
        var service = new SignalingService();
        var host = CreateHost([service], edition);

        await host.StartAsync(CancellationToken.None);
        await Task.Delay(100);

        service.Started.Task.IsCompleted.Should().BeFalse();
        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DoesNotStart_WhenDisabledByConfig()
    {
        var service = new SignalingService();
        var host = CreateHost([service], enabled: false);

        await host.StartAsync(CancellationToken.None);
        await Task.Delay(100);

        service.Started.Task.IsCompleted.Should().BeFalse();
        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FaultingService_IsIsolated_HostStartStopSucceeds()
    {
        var host = CreateHost([new FaultingService()]);

        // A service that throws immediately must not surface from Start, and Stop must complete.
        await host.StartAsync(CancellationToken.None);
        await host.StopAsync(CancellationToken.None);
    }

    [Plugin("signaling-bg", "1.0.0", Capabilities = PluginCapability.BackgroundExecution)]
    private sealed class SignalingService : IPluginBackgroundService
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool SawCancellation { get; private set; }

        public async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Started.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                SawCancellation = true;
            }
        }
    }

    [Plugin("faulting-bg", "1.0.0", Capabilities = PluginCapability.BackgroundExecution)]
    private sealed class FaultingService : IPluginBackgroundService
    {
        public Task ExecuteAsync(CancellationToken stoppingToken)
            => throw new InvalidOperationException("boom");
    }
}

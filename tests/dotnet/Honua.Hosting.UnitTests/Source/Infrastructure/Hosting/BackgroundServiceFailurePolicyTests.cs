// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Tests.Infrastructure.Hosting;

// Honua's background-service failure policy is fail-fast: the host keeps the default
// BackgroundServiceExceptionBehavior.StopHost, so an exception escaping ExecuteAsync stops the
// process, and since .NET 11 the run task faults, so the process exits non-zero and the
// supervisor (compose restart, ECS, Kubernetes) restarts it. Services that must survive a
// failure catch it inside their own loop (for example LicenseCapacityMeter).
public sealed class BackgroundServiceFailurePolicyTests
{
    [UnitTest]
    public async Task FailingBackgroundService_FaultsTheHostRun()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddHostedService<FailingService>();
        using var host = builder.Build();

        // Cancelling stops the host cleanly, so a regression fails the assertion instead of hanging.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = () => host.RunAsync(timeout.Token);

        (await run.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage(FailingService.Message);
    }

    private sealed class FailingService : BackgroundService
    {
        public const string Message = "background service failed";

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();
            throw new InvalidOperationException(Message);
        }
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Server.Features.Admin.Routing;
using Honua.TestKit;
using Honua.TestKit.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Honua.Server.Tests.Features.Admin.Routing;

// One network admin application per test class on the production PostgreSQL job substrate.
// The tests run the rebuild executor and the reconciler themselves, so the job worker and
// the periodic rebuild reconciler are not hosted: either would claim the same attempt first.
public abstract class NetworkJobApplication(string adminPassword) : IAsyncLifetime
{
    public WebAppFixture Fixture { get; } = new WebAppFixture().WithProductionJobSubstrate()
        .ConfigureServices(RemoveCompetingLoops)
        .UseSeed("tests/seed/server.yaml")
        .ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", adminPassword);
            builder.UseSetting("Licensing:DevGrantEdition", "Pro");
        });

    public async Task InitializeAsync()
    {
        await Fixture.InitializeAsync();
        await Fixture.Postgres.DataSource.ClearJobSubstrateAsync();
    }

    public Task DisposeAsync() => Fixture.DisposeAsync();

    private static void RemoveCompetingLoops(IServiceCollection services)
    {
        var loops = services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                && (descriptor.ImplementationType == typeof(JobExecutionService)
                    || descriptor.ImplementationType == typeof(NetworkTopologyRebuildReconcilerBackgroundService)))
            .ToList();
        foreach (var loop in loops)
        {
            services.Remove(loop);
        }
    }
}

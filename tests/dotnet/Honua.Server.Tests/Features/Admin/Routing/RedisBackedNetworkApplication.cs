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

// One network admin application per test class, started on first use: its Redis connection
// comes from the collection fixture, which xunit 2 cannot inject into a class fixture.
public abstract class RedisBackedNetworkApplication(string adminPassword) : IAsyncLifetime
{
    private readonly SemaphoreSlim _start = new(1, 1);
    private WebAppFixture? _fixture;

    public async Task<WebAppFixture> GetAsync(RedisFixture redis)
    {
        await _start.WaitAsync();
        try
        {
            if (_fixture is null)
            {
                var fixture = Create(redis.ConnectionString);
                try
                {
                    await fixture.InitializeAsync();
                }
                catch
                {
                    await fixture.DisposeAsync();
                    throw;
                }

                _fixture = fixture;
            }

            return _fixture;
        }
        finally
        {
            _start.Release();
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
        }

        _start.Dispose();
    }

    // Rebuild submission creates a durable ExecutionJobRecord through the shared job
    // infrastructure, which the production composition root wires only when a Redis
    // connection and a Redis-cache license entitlement are present. The tests run the
    // rebuild executor and the reconciler themselves, so the job worker and the periodic
    // reconciler are not hosted: either would claim the same attempt first.
    private WebAppFixture Create(string redisConnectionString) =>
        new WebAppFixture().WithProductionJobSubstrate()
            .ConfigureServices(RemoveCompetingLoops)
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", adminPassword);
                builder.UseSetting("ConnectionStrings:redis", redisConnectionString);
                builder.UseSetting("Licensing:DevGrantEdition", "Pro");
            });

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

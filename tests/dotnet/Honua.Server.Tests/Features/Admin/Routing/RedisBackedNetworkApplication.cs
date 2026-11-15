// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit;
using Honua.TestKit.Extensions;
using Microsoft.AspNetCore.Hosting;

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
    // connection and a Redis-cache license entitlement are present.
    private WebAppFixture Create(string redisConnectionString) =>
        new WebAppFixture().WithProductionJobSubstrate()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", adminPassword);
                builder.UseSetting("ConnectionStrings:redis", redisConnectionString);
                builder.UseSetting("Licensing:DevGrantEdition", "Pro");
            });
}

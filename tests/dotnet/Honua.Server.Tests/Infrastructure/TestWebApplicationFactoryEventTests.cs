// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Events;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Infrastructure;

public sealed class TestWebApplicationFactoryEventTests
{
    [UnitTest]
    public async Task Create_DefaultHost_PublishesAndReadsEventsWithoutDatabase()
    {
        await using var factory = new TestWebApplicationFactory();
        var publisher = factory.Services.GetRequiredService<IFeatureChangeEventPublisher>();
        var store = factory.Services.GetRequiredService<IFeatureChangeEventStore>();
        var health = factory.Services.GetRequiredService<IFeatureChangeEventStoreHealth>();
        Assert.True(health.CanPersistEvents);
        Assert.False(health.IsUsingInMemoryFallback);

        await publisher.PublishAsync(CreateRequest(1));
        await publisher.PublishStrictAsync(CreateRequest(2));

        var events = await store.QueryAsync(new FeatureChangeEventQuery(null, null, null, 10));
        Assert.Equal(new long[] { 1, 2 }, events.Select(e => e.ObjectId));
        Assert.Equal(2, await store.GetCurrentCursorAsync());
        Assert.All(events, e => Assert.Equal("insert", e.Operation));

        using var client = factory.CreateClient();
        using var readiness = await client.GetAsync("/healthz/ready");
        readiness.EnsureSuccessStatusCode();
    }

    [UnitTest]
    public async Task Create_SeparateHosts_DoNotShareEvents()
    {
        await using var first = new TestWebApplicationFactory();
        await using var second = new TestWebApplicationFactory();
        await first.Services.GetRequiredService<IFeatureChangeEventPublisher>().PublishAsync(CreateRequest(1));

        Assert.Equal(0, await second.Services.GetRequiredService<IFeatureChangeEventStore>().GetCurrentCursorAsync());
    }

    [UnitTest]
    public async Task Create_ExplicitEventOverrides_PreservesOverrides()
    {
        var replacement = new FakeFeatureChangeEventStore();
        await using var baseFactory = new TestWebApplicationFactory();
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IFeatureChangeEventStore>();
                services.AddSingleton<IFeatureChangeEventStore>(replacement);
                services.RemoveAll<IFeatureChangeEventStoreHealth>();
                services.AddSingleton<IFeatureChangeEventStoreHealth>(replacement);
                services.RemoveAll<IFeatureChangeEventPublisher>();
                services.AddSingleton<IFeatureChangeEventPublisher>(replacement);
            }));

        Assert.Same(replacement, factory.Services.GetRequiredService<IFeatureChangeEventStore>());
        Assert.Same(replacement, factory.Services.GetRequiredService<IFeatureChangeEventStoreHealth>());
        Assert.Same(replacement, factory.Services.GetRequiredService<IFeatureChangeEventPublisher>());
    }

    private static FeatureChangeEventRequest CreateRequest(long objectId) => new()
    {
        ServiceId = "test-service",
        LayerId = 0,
        ObjectId = objectId,
        Operation = "create",
        Protocol = "test",
        RequestId = "test-request"
    };
}

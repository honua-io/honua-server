// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Testcontainers.Redis;

namespace Honua.Hosting.ComponentTests;

/// <summary>Dedicated AI authority with stronger persistence than the general job Redis fixture.</summary>
public sealed class AiAdmissionRedisFixture : IAsyncLifetime
{
    public const string CollectionName = "AI admission Redis";
    private readonly RedisContainer _container = new RedisBuilder("redis:7.2-alpine")
        .WithCommand("redis-server", "--appendonly", "yes", "--appendfsync", "always", "--no-appendfsync-on-rewrite", "no",
            "--save", "", "--maxmemory-policy", "noeviction").Build();
    public string ConnectionString => _container.GetConnectionString();
    public Task InitializeAsync() => _container.StartAsync();
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

/// <summary>Serializes authority configuration tests on their dedicated Redis instance.</summary>
[CollectionDefinition(AiAdmissionRedisFixture.CollectionName)]
public sealed class AiAdmissionRedisCollection : ICollectionFixture<AiAdmissionRedisFixture>;

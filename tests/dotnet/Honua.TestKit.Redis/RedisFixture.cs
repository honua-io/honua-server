// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Testcontainers.Redis;
using Xunit;

namespace Honua.TestKit;

/// <summary>
/// Shared Redis fixture for integration tests.
/// Uses Testcontainers to manage container lifecycle.
/// </summary>
/// <remarks>
/// The container runs the durable configuration the server's startup attestation requires
/// (honua-server#3903): AOF persistence with a bounded fsync and <c>noeviction</c>. Without it
/// the composition root refuses to advertise the durable job substrate, so every test that
/// submits a durable job through the real host gets HTTP 503 instead of exercising the path
/// under test. An external <c>HONUA_TEST_REDIS_URL</c> server must be configured the same way.
/// </remarks>
public sealed class RedisFixture : IAsyncLifetime
{
    /// <summary>
    /// Canonical xUnit collection name for tests that share this fixture.
    /// </summary>
    public const string CollectionName = "Redis";

    // The state object is process-wide and every mutation is serialized by _sharedLock.
    private static readonly SemaphoreSlim _sharedLock = new(1, 1);
    private static readonly RedisSharedState SharedState = new();

    private sealed class RedisSharedState
    {
        public RedisContainer? SharedContainer { get; set; }
        public string? SharedConnectionString { get; set; }
        public int SharedRefCount { get; set; }
        public bool SharedInitialized { get; set; }
    }
    private const string ExternalConnectionStringEnv = "HONUA_TEST_REDIS_URL";
    private string? _connectionString;

    public string ConnectionString => _connectionString ?? throw new InvalidOperationException("Redis fixture not initialized.");

    public async Task InitializeAsync()
    {
        await _sharedLock.WaitAsync();
        try
        {
            if (!SharedState.SharedInitialized)
            {
                var externalConnectionString = Environment.GetEnvironmentVariable(ExternalConnectionStringEnv);
                if (string.IsNullOrWhiteSpace(externalConnectionString))
                {
                    SharedState.SharedContainer = new RedisBuilder("redis:7.2-alpine")
                        .WithCommand(
                            "redis-server",
                            "--appendonly",
                            "yes",
                            "--appendfsync",
                            "everysec",
                            "--save",
                            "",
                            "--maxmemory-policy",
                            "noeviction")
                        .Build();
                    await SharedState.SharedContainer.StartAsync();
                    SharedState.SharedConnectionString = SharedState.SharedContainer.GetConnectionString();
                }
                else
                {
                    SharedState.SharedConnectionString = externalConnectionString;
                }

                SharedState.SharedInitialized = true;
            }

            SharedState.SharedRefCount++;
            _connectionString = SharedState.SharedConnectionString;
        }
        finally
        {
            _sharedLock.Release();
        }
    }

    public async Task DisposeAsync()
    {
        await _sharedLock.WaitAsync();
        try
        {
            if (SharedState.SharedRefCount > 0)
            {
                SharedState.SharedRefCount--;
            }

            if (SharedState.SharedRefCount == 0 && SharedState.SharedInitialized)
            {
                if (SharedState.SharedContainer is not null)
                {
                    await SharedState.SharedContainer.DisposeAsync();
                }

                SharedState.SharedContainer = null;
                SharedState.SharedConnectionString = null;
                SharedState.SharedInitialized = false;
            }
        }
        finally
        {
            _sharedLock.Release();
        }
    }
}

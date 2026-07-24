// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Testcontainers.Redis;
using Xunit;

namespace Honua.TestKit;

/// <summary>
/// Shared Redis fixture for integration tests.
/// Uses Testcontainers to manage container lifecycle.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
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

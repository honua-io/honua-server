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
    private static readonly SemaphoreSlim _sharedLock = new(1, 1);
    private static RedisContainer? _sharedContainer;
    private static string? _sharedConnectionString;
    private static int _sharedRefCount;
    private static bool _sharedInitialized;
    private const string ExternalConnectionStringEnv = "HONUA_TEST_REDIS_URL";
    private string? _connectionString;

    public string ConnectionString => _connectionString ?? throw new InvalidOperationException("Redis fixture not initialized.");

    public async Task InitializeAsync()
    {
        await _sharedLock.WaitAsync();
        try
        {
            if (!_sharedInitialized)
            {
                var externalConnectionString = Environment.GetEnvironmentVariable(ExternalConnectionStringEnv);
                if (string.IsNullOrWhiteSpace(externalConnectionString))
                {
                    _sharedContainer = new RedisBuilder()
                        .WithImage("redis:7.2-alpine")
                        .Build();
                    await _sharedContainer.StartAsync();
                    _sharedConnectionString = _sharedContainer.GetConnectionString();
                }
                else
                {
                    _sharedConnectionString = externalConnectionString;
                }

                _sharedInitialized = true;
            }

            _sharedRefCount++;
            _connectionString = _sharedConnectionString;
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
            if (_sharedRefCount > 0)
            {
                _sharedRefCount--;
            }

            if (_sharedRefCount == 0 && _sharedInitialized)
            {
                if (_sharedContainer is not null)
                {
                    await _sharedContainer.DisposeAsync();
                }

                _sharedContainer = null;
                _sharedConnectionString = null;
                _sharedInitialized = false;
            }
        }
        finally
        {
            _sharedLock.Release();
        }
    }
}

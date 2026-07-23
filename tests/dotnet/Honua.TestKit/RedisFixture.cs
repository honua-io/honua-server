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
    // These static fields back an intentional process-wide, ref-counted shared container:
    // every RedisFixture instance (one per xUnit collection) mutates the same statics
    // under _sharedLock so only the first InitializeAsync starts the container and only the
    // last DisposeAsync tears it down. Writing static state from instance lifecycle
    // methods is the design, not a bug.
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
                    // codeql[cs/static-field-written-by-instance] -- the instance lifecycle intentionally coordinates shared process-wide state.
                    _sharedContainer = new RedisBuilder("redis:7.2-alpine")
                        .Build();
                    await _sharedContainer.StartAsync();
                    // codeql[cs/static-field-written-by-instance] -- the instance lifecycle intentionally coordinates shared process-wide state.
                    _sharedConnectionString = _sharedContainer.GetConnectionString();
                }
                else
                {
                    // codeql[cs/static-field-written-by-instance] -- the instance lifecycle intentionally coordinates shared process-wide state.
                    _sharedConnectionString = externalConnectionString;
                }

                // codeql[cs/static-field-written-by-instance] -- the instance lifecycle intentionally coordinates shared process-wide state.
                _sharedInitialized = true;
            }

            // codeql[cs/static-field-written-by-instance] -- the instance lifecycle intentionally coordinates shared process-wide state.
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
                // codeql[cs/static-field-written-by-instance] -- the instance lifecycle intentionally coordinates shared process-wide state.
                _sharedRefCount--;
            }

            if (_sharedRefCount == 0 && _sharedInitialized)
            {
                if (_sharedContainer is not null)
                {
                    await _sharedContainer.DisposeAsync();
                }

                // codeql[cs/static-field-written-by-instance] -- the instance lifecycle intentionally coordinates shared process-wide state.
                _sharedContainer = null;
                // codeql[cs/static-field-written-by-instance] -- the instance lifecycle intentionally coordinates shared process-wide state.
                _sharedConnectionString = null;
                // codeql[cs/static-field-written-by-instance] -- the instance lifecycle intentionally coordinates shared process-wide state.
                _sharedInitialized = false;
            }
        }
        finally
        {
            _sharedLock.Release();
        }
    }
}

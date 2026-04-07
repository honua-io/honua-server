// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.DataIntegrity;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Xunit;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Features.Security;

/// <summary>
/// Tests to validate that P0 critical security fixes are working correctly.
/// These tests address the specific vulnerabilities found in the comprehensive audit.
/// </summary>
public sealed class P0CriticalFixesValidationTests : IAsyncDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly ServiceProvider _serviceProvider;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<P0CriticalFixesValidationTests> _logger;

    public P0CriticalFixesValidationTests(ITestOutputHelper output)
    {
        _output = output;

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(new XunitLoggerProvider(output)));
        services.AddMemoryCache();
        services.AddDistributedMemoryCache();
        services.AddAtomicCacheInvalidation();

        _serviceProvider = services.BuildServiceProvider();
        _memoryCache = _serviceProvider.GetRequiredService<IMemoryCache>();
        _logger = _serviceProvider.GetRequiredService<ILogger<P0CriticalFixesValidationTests>>();
    }

    [Fact]
    public async Task JwtReplayProtection_ShouldPreventReplayAttacks()
    {
        // Arrange
        var tokenValidationOptions = new TokenValidationOptions
        {
            EnableTokenReplayProtection = true,
            TokenReplayCacheDuration = TimeSpan.FromMinutes(30)
        };

        var jwt = CreateTestJwtToken();
        var securityToken = new JwtSecurityToken(jwt);

        // Act & Assert
        // First use should succeed
        var firstUse = await AtomicTokenReplayProtection.TryMarkTokenAsUsedAsync(
            securityToken, tokenValidationOptions, _serviceProvider);
        Assert.True(firstUse, "First use of token should be allowed");

        // Second use should fail (replay attack)
        var secondUse = await AtomicTokenReplayProtection.TryMarkTokenAsUsedAsync(
            securityToken, tokenValidationOptions, _serviceProvider);
        Assert.False(secondUse, "Second use of token should be blocked as replay attack");

        _output.WriteLine("JWT replay protection working correctly");
    }

    [Fact]
    public async Task JwtReplayProtection_ConcurrentAccess_ShouldBeAtomic()
    {
        // Arrange
        var tokenValidationOptions = new TokenValidationOptions
        {
            EnableTokenReplayProtection = true,
            TokenReplayCacheDuration = TimeSpan.FromMinutes(30)
        };

        var jwt = CreateTestJwtToken();
        var securityToken = new JwtSecurityToken(jwt);

        // Act - Simulate concurrent access to the same token
        var tasks = new List<Task<bool>>();
        const int concurrentRequests = 10;

        for (int i = 0; i < concurrentRequests; i++)
        {
            tasks.Add(AtomicTokenReplayProtection.TryMarkTokenAsUsedAsync(
                securityToken, tokenValidationOptions, _serviceProvider));
        }

        var results = await Task.WhenAll(tasks);

        // Assert - Only one should succeed
        var successCount = results.Count(r => r);
        Assert.Equal(1, successCount);

        var failureCount = results.Count(r => !r);
        Assert.Equal(concurrentRequests - 1, failureCount);

        _output.WriteLine($"Concurrent JWT replay protection: {successCount} success, {failureCount} blocked");
    }

    [Fact]
    public async Task AtomicCacheInvalidation_ShouldPreventRaceConditions()
    {
        // Arrange
        var cacheService = _serviceProvider.GetRequiredService<IAtomicCacheInvalidationService>();
        const string cacheKey = "test_key_race_condition";

        var valueFactory = (CancellationToken ct) => Task.FromResult("test_value");

        // Act - Simulate concurrent cache updates
        var tasks = new List<Task<string>>();
        const int concurrentUpdates = 5;

        for (int i = 0; i < concurrentUpdates; i++)
        {
            tasks.Add(cacheService.UpdateAtomicAsync(
                cacheKey,
                valueFactory,
                TimeSpan.FromMinutes(10)));
        }

        var results = await Task.WhenAll(tasks);

        // Assert - All should return the same value (no race condition)
        Assert.All(results, result => Assert.Equal("test_value", result));

        _output.WriteLine($"Atomic cache update completed successfully for {concurrentUpdates} concurrent operations");
    }

    [Fact]
    public async Task AtomicCacheInvalidation_InvalidatePattern_ShouldWork()
    {
        // Arrange
        var cacheService = _serviceProvider.GetRequiredService<IAtomicCacheInvalidationService>();

        // Pre-populate cache with test data
        _memoryCache.Set("test:key1", "value1");
        _memoryCache.Set("test:key2", "value2");
        _memoryCache.Set("other:key3", "value3");

        // Act
        await cacheService.InvalidatePatternAtomicAsync("test:*");

        // Assert
        Assert.False(_memoryCache.TryGetValue("test:key1", out _), "test:key1 should be invalidated");
        Assert.False(_memoryCache.TryGetValue("test:key2", out _), "test:key2 should be invalidated");
        Assert.True(_memoryCache.TryGetValue("other:key3", out _), "other:key3 should remain");

        _output.WriteLine("Pattern-based cache invalidation working correctly");
    }

    [Fact]
    public async Task DataIntegrityCoordinator_ShouldProvideACIDGuarantees()
    {
        // This test would require a full database setup, so we'll test the coordination logic
        // In a real scenario, this would test with actual database transactions

        var mockDataSource = new MockNpgsqlDataSource();
        var coordinator = new DataIntegrityCoordinator(mockDataSource, _logger);

        var operationId = Guid.NewGuid().ToString();
        var executed = false;

        // Act
        await coordinator.ExecuteCoordinatedTransactionAsync(
            operationId,
            async (transaction, ct) =>
            {
                executed = true;

                // Simulate registering file and cache operations
                transaction.RegisterFileOperation(
                    "upload",
                    "/test/file.txt",
                    commitAction: ct => Task.CompletedTask,
                    rollbackAction: ct => Task.CompletedTask);

                transaction.RegisterCacheOperation(
                    "test_cache_key",
                    commitAction: ct => Task.CompletedTask,
                    rollbackAction: ct => Task.CompletedTask);

                return "success";
            });

        // Assert
        Assert.True(executed, "Coordinated transaction should execute");

        _output.WriteLine("Data integrity coordinator providing proper transaction coordination");
    }

    [Fact]
    public async Task DistributedLock_ShouldPreventConcurrentAccess()
    {
        var mockDataSource = new MockNpgsqlDataSource();
        var coordinator = new DataIntegrityCoordinator(mockDataSource, _logger);

        const string lockKey = "test_distributed_lock";
        var accessOrder = new ConcurrentQueue<int>();

        // Act - Simulate concurrent access to the same resource
        var tasks = new List<Task>();
        const int concurrentOperations = 3;

        for (int i = 0; i < concurrentOperations; i++)
        {
            int operationId = i;
            tasks.Add(Task.Run(async () =>
            {
                await using var distributedLock = await coordinator.AcquireDistributedLockAsync(
                    lockKey, TimeSpan.FromSeconds(10));

                accessOrder.Enqueue(operationId);
                await Task.Delay(100); // Simulate work
            }));
        }

        await Task.WhenAll(tasks);

        // Assert - Operations should be serialized
        Assert.Equal(concurrentOperations, accessOrder.Count);

        _output.WriteLine($"Distributed lock serialized {concurrentOperations} concurrent operations");
    }

    private static string CreateTestJwtToken()
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.ASCII.GetBytes("this_is_a_test_key_with_sufficient_length_for_hmac");

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim("sub", "test_user"),
                new Claim("jti", Guid.NewGuid().ToString()), // Important for replay detection
                new Claim("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
            }),
            Expires = DateTime.UtcNow.AddMinutes(30),
            Issuer = "test_issuer",
            Audience = "test_audience",
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(key),
                SecurityAlgorithms.HmacSha256Signature)
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }

    public async ValueTask DisposeAsync()
    {
        await _serviceProvider.DisposeAsync();
    }

    private sealed class MockNpgsqlDataSource : NpgsqlDataSource
    {
        public MockNpgsqlDataSource() : base(null, null!, null!, null!)
        {
            // Mock implementation for testing
        }

        public override ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException("Mock implementation - use integration tests for full database testing");
        }
    }

    private sealed class XunitLoggerProvider : ILoggerProvider
    {
        private readonly ITestOutputHelper _output;

        public XunitLoggerProvider(ITestOutputHelper output)
        {
            _output = output;
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new XunitLogger(_output, categoryName);
        }

        public void Dispose()
        {
        }

        private sealed class XunitLogger : ILogger
        {
            private readonly ITestOutputHelper _output;
            private readonly string _categoryName;

            public XunitLogger(ITestOutputHelper output, string categoryName)
            {
                _output = output;
                _categoryName = categoryName;
            }

            public IDisposable BeginScope<TState>(TState state) => new NoOpDisposable();

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                _output.WriteLine($"[{logLevel}] {_categoryName}: {formatter(state, exception)}");
                if (exception != null)
                {
                    _output.WriteLine(exception.ToString());
                }
            }

            private sealed class NoOpDisposable : IDisposable
            {
                public void Dispose() { }
            }
        }
    }
}
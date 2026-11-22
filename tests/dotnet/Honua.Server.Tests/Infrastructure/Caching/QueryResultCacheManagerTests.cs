// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Monitoring;
using Honua.Server.Tests.Features.Admin;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Caching;

public sealed class QueryResultCacheManagerTests
{
    [Fact]
    public async Task GetOrExecuteAsync_ByDefault_DoesNotCacheResults()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        using var manager = CreateManager(memoryCache, new QueryResultCacheOptions());
        var executions = 0;

        var first = await manager.GetOrExecuteAsync("query:spatial", () => Task.FromResult(++executions));
        var second = await manager.GetOrExecuteAsync("query:spatial", () => Task.FromResult(++executions));

        first.Should().Be(1);
        second.Should().Be(2);
        executions.Should().Be(2);
    }

    [Fact]
    public async Task GetOrExecuteAsync_WhenEnabled_CachesResults()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        using var manager = CreateManager(
            memoryCache,
            new QueryResultCacheOptions
            {
                Enabled = true
            });
        var executions = 0;

        var first = await manager.GetOrExecuteAsync("query:nonspatial", () => Task.FromResult(++executions));
        var second = await manager.GetOrExecuteAsync("query:nonspatial", () => Task.FromResult(++executions));

        first.Should().Be(1);
        second.Should().Be(1);
        executions.Should().Be(1);
    }

    [UnitTest]
    public async Task InvalidateAsync_WithCrLfPattern_SanitizesPatternInLog()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        using var logCapture = new NetworkTopologyEditAdminEndpointsTests.CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logCapture));
        var logger = loggerFactory.CreateLogger<QueryResultCacheManager>();
        using var manager = new QueryResultCacheManager(
            memoryCache,
            logger,
            Options.Create(new QueryResultCacheOptions { Enabled = true }));

        var removed = await manager.InvalidateAsync("eval3-noop\r\n[ERR] FORGED_CACHE_LINE");

        removed.Should().Be(0);
        logCapture.Messages.Should().ContainSingle();
        logCapture.Messages[0].Should().NotContain("\r").And.NotContain("\n");
        logCapture.Messages[0].Should().Contain("eval3-noop__[ERR] FORGED_CACHE_LINE");
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("prefix\r\n[ERR] forged")]
    [InlineData("prefix\nforged")]
    [InlineData("prefix\0null")]
    public void ValidateInvalidationPattern_RejectsBlankOrControlCharacters(string? pattern)
    {
        EnhancedPerformanceEndpoints.ValidateInvalidationPattern(pattern)
            .Should().NotBeNullOrWhiteSpace();
    }

    [UnitTest]
    public void ValidateInvalidationPattern_RejectsOverlongPattern_AndAcceptsNormalPattern()
    {
        var tooLong = new string('a', EnhancedPerformanceEndpoints.MaxCacheInvalidationPatternLength + 1);
        EnhancedPerformanceEndpoints.ValidateInvalidationPattern(tooLong)
            .Should().NotBeNullOrWhiteSpace();

        EnhancedPerformanceEndpoints.ValidateInvalidationPattern("query:spatial:*")
            .Should().BeNull();
    }

    private static QueryResultCacheManager CreateManager(IMemoryCache memoryCache, QueryResultCacheOptions options)
        => new(
            memoryCache,
            NullLogger<QueryResultCacheManager>.Instance,
            Options.Create(options));
}

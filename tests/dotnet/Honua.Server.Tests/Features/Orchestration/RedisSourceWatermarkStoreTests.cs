// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Migration.Watermark;
using Honua.Server.Features.Orchestration;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Orchestration;

/// <summary>
/// Redis integration tests for the durable per-pipeline+source watermark store backing
/// incremental ("changed-since") extraction.
/// </summary>
[Collection("Redis")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
[Operation(Operations.TestInfrastructure)]
public sealed class RedisSourceWatermarkStoreTests
{
    private readonly RedisFixture _redis;

    public RedisSourceWatermarkStoreTests(RedisFixture redis)
    {
        _redis = redis;
    }

    private static SourceWatermark Mark(string pipeline, string source, DateTimeOffset? at, DateTimeOffset updated) => new()
    {
        PipelineId = pipeline,
        SourceId = source,
        Kind = WatermarkKind.EditTimestamp,
        Value = at is { } v ? SourceWatermark.Encode(v) : null,
        UpdatedAt = updated
    };

    [IntegrationTest]
    public async Task Get_WhenNeverExtracted_ReturnsNull()
    {
        using var multiplexer = ConnectionMultiplexer.Connect(_redis.ConnectionString);
        var store = new RedisSourceWatermarkStore(multiplexer);
        var pipeline = $"wf-{Guid.NewGuid():N}";

        var result = await store.GetAsync(pipeline, "src");

        result.Should().BeNull();
    }

    [IntegrationTest]
    public async Task Advance_PersistsAndReadsBackWatermark()
    {
        using var multiplexer = ConnectionMultiplexer.Connect(_redis.ConnectionString);
        var store = new RedisSourceWatermarkStore(multiplexer);
        var pipeline = $"wf-{Guid.NewGuid():N}";
        var at = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        await store.AdvanceAsync(Mark(pipeline, "src", at, at));

        var stored = await store.GetAsync(pipeline, "src");
        stored.Should().NotBeNull();
        stored!.AsTimestamp().Should().Be(at);
        stored.Kind.Should().Be(WatermarkKind.EditTimestamp);
    }

    [IntegrationTest]
    public async Task Advance_IsMonotonic_NeverRewinds()
    {
        using var multiplexer = ConnectionMultiplexer.Connect(_redis.ConnectionString);
        var store = new RedisSourceWatermarkStore(multiplexer);
        var pipeline = $"wf-{Guid.NewGuid():N}";
        var later = new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);
        var earlier = new DateTimeOffset(2026, 6, 5, 0, 0, 0, TimeSpan.Zero);

        await store.AdvanceAsync(Mark(pipeline, "src", later, later));

        // A competing/out-of-order completion tries to set an earlier mark — must be ignored.
        var effective = await store.AdvanceAsync(Mark(pipeline, "src", earlier, earlier));
        effective.AsTimestamp().Should().Be(later);

        var stored = await store.GetAsync(pipeline, "src");
        stored!.AsTimestamp().Should().Be(later);
    }

    [IntegrationTest]
    public async Task Advance_ResumesFromPersistedMark_AfterReopen()
    {
        var pipeline = $"wf-{Guid.NewGuid():N}";
        var at = new DateTimeOffset(2026, 6, 7, 12, 0, 0, TimeSpan.Zero);

        using (var multiplexerA = ConnectionMultiplexer.Connect(_redis.ConnectionString))
        {
            var storeA = new RedisSourceWatermarkStore(multiplexerA);
            await storeA.AdvanceAsync(Mark(pipeline, "src", at, at));
        }

        // Simulate process restart: a fresh connection/store must resume from the durable mark.
        using var multiplexerB = ConnectionMultiplexer.Connect(_redis.ConnectionString);
        var storeB = new RedisSourceWatermarkStore(multiplexerB);
        var resumed = await storeB.GetAsync(pipeline, "src");

        resumed.Should().NotBeNull();
        resumed!.AsTimestamp().Should().Be(at);
    }

    [IntegrationTest]
    public async Task Clear_RemovesWatermark_NextPullIsFullScan()
    {
        using var multiplexer = ConnectionMultiplexer.Connect(_redis.ConnectionString);
        var store = new RedisSourceWatermarkStore(multiplexer);
        var pipeline = $"wf-{Guid.NewGuid():N}";
        var at = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        await store.AdvanceAsync(Mark(pipeline, "src", at, at));
        await store.ClearAsync(pipeline, "src");

        (await store.GetAsync(pipeline, "src")).Should().BeNull();
    }
}

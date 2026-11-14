// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Migration.Watermark;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Orchestration;

[Collection(WebAppFixtureJobSubstrateExtensions.JobSubstrateCollection)]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresSourceWatermarkStoreTests
{
    private static readonly DateTimeOffset Timestamp =
        new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero).AddTicks(7);

    [IntegrationTest]
    public async Task AdvanceAsync_WithoutRedis_IsReadableAfterTheWritingHostStops()
    {
        var mark = Mark($"pipeline-{Guid.NewGuid():N}", "source", Timestamp);
        await using (var writer = CreateHost())
        {
            await writer.InitializeAsync();
            var store = writer.GetService<ISourceWatermarkStore>();
            (await store.GetAsync(mark.PipelineId, mark.SourceId)).Should().BeNull();
            await store.AdvanceAsync(mark);
        }

        await using var reader = CreateHost();
        await reader.InitializeAsync();
        var reopened = reader.GetService<ISourceWatermarkStore>();
        try
        {
            var persisted = await reopened.GetAsync(mark.PipelineId, mark.SourceId);
            persisted.Should().BeEquivalentTo(mark);
        }
        finally
        {
            await reopened.ClearAsync(mark.PipelineId, mark.SourceId);
        }
    }

    [IntegrationTest]
    public async Task AdvanceAsync_ConcurrentOlderCompletions_NeverRewindsTheWatermark()
    {
        await using var host = CreateHost();
        await host.InitializeAsync();
        var store = host.GetService<ISourceWatermarkStore>();
        var latest = Mark($"pipeline-{Guid.NewGuid():N}", "source", Timestamp);
        try
        {
            await store.AdvanceAsync(latest);
            var results = await Task.WhenAll(Enumerable.Range(1, 8).Select(offset =>
                store.AdvanceAsync(Mark(latest.PipelineId, latest.SourceId,
                    latest.AsTimestamp()!.Value.AddMinutes(-offset)))));

            results.Should().OnlyContain(result => result.Value == latest.Value);
            (await store.GetAsync(latest.PipelineId, latest.SourceId))!
                .AsTimestamp().Should().Be(latest.AsTimestamp());
        }
        finally
        {
            await store.ClearAsync(latest.PipelineId, latest.SourceId);
        }
    }

    [IntegrationTest]
    public async Task AdvanceAsync_CompetingInitialCompletions_PersistsTheGreatestFileTimestamp()
    {
        await using var host = CreateHost();
        await host.InitializeAsync();
        var store = host.GetService<ISourceWatermarkStore>();
        var initial = Mark($"pipeline-{Guid.NewGuid():N}", "source", Timestamp) with
        {
            Kind = WatermarkKind.FileModifiedTime
        };
        try
        {
            // Newest first, so a last-writer-wins upsert would leave an older timestamp behind.
            await Task.WhenAll(Enumerable.Range(0, 8).Reverse().Select(offset => store.AdvanceAsync(initial with
            {
                Value = SourceWatermark.Encode(Timestamp.AddMinutes(offset))
            })));

            var persisted = await store.GetAsync(initial.PipelineId, initial.SourceId);
            persisted.Should().NotBeNull();
            persisted!.Kind.Should().Be(WatermarkKind.FileModifiedTime);
            persisted.AsTimestamp().Should().Be(Timestamp.AddMinutes(7));
        }
        finally
        {
            await store.ClearAsync(initial.PipelineId, initial.SourceId);
        }
    }

    [IntegrationTest]
    public async Task AdvanceAsync_OneTickNewerTimestamp_PreservesTheAdvance()
    {
        await using var host = CreateHost();
        await host.InitializeAsync();
        var store = host.GetService<ISourceWatermarkStore>();
        var initial = Mark($"pipeline-{Guid.NewGuid():N}", "source", Timestamp);
        var next = Mark(initial.PipelineId, initial.SourceId, Timestamp.AddTicks(1));
        try
        {
            await store.AdvanceAsync(initial);
            (await store.AdvanceAsync(next)).Should().BeEquivalentTo(next);
            (await store.GetAsync(initial.PipelineId, initial.SourceId)).Should().BeEquivalentTo(next);
        }
        finally
        {
            await store.ClearAsync(initial.PipelineId, initial.SourceId);
        }
    }

    [IntegrationTest]
    public async Task ClearAsync_CompositeKeysWithSeparators_RemovesOnlyTheRequestedSource()
    {
        await using var host = CreateHost();
        await host.InitializeAsync();
        var store = host.GetService<ISourceWatermarkStore>();
        var pipeline = $"pipeline-{Guid.NewGuid():N}";
        var first = Mark($"{pipeline}:branch", "source", Timestamp);
        var second = Mark(pipeline, "branch:source", Timestamp.AddMinutes(1));
        try
        {
            await store.AdvanceAsync(first);
            await store.AdvanceAsync(second);
            await store.ClearAsync(first.PipelineId, first.SourceId);

            (await store.GetAsync(first.PipelineId, first.SourceId)).Should().BeNull();
            (await store.GetAsync(second.PipelineId, second.SourceId)).Should().BeEquivalentTo(second);
        }
        finally
        {
            await store.ClearAsync(first.PipelineId, first.SourceId);
            await store.ClearAsync(second.PipelineId, second.SourceId);
        }
    }

    [IntegrationTheory]
    [InlineData(null)]
    [InlineData("opaque-cursor")]
    public async Task AdvanceAsync_NonTimestampValue_PreservesTheRequestedValue(string? value)
    {
        await using var host = CreateHost();
        await host.InitializeAsync();
        var store = host.GetService<ISourceWatermarkStore>();
        var initial = Mark($"pipeline-{Guid.NewGuid():N}", "source", Timestamp);
        var requested = initial with { Value = value, UpdatedAt = null };
        try
        {
            await store.AdvanceAsync(initial);
            (await store.AdvanceAsync(requested)).Should().BeEquivalentTo(requested);
            (await store.GetAsync(initial.PipelineId, initial.SourceId)).Should().BeEquivalentTo(requested);
        }
        finally
        {
            await store.ClearAsync(initial.PipelineId, initial.SourceId);
        }
    }

    [IntegrationTheory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task AdvanceAsync_EqualOrOlderTimestamp_RefreshesOnlyTheTouchTime(int offset)
    {
        await using var host = CreateHost();
        await host.InitializeAsync();
        var store = host.GetService<ISourceWatermarkStore>();
        var initial = Mark($"pipeline-{Guid.NewGuid():N}", "source", Timestamp);
        var requested = initial with
        {
            Kind = WatermarkKind.FileModifiedTime,
            Value = SourceWatermark.Encode(Timestamp.AddMinutes(offset)),
            UpdatedAt = null
        };
        try
        {
            await store.AdvanceAsync(initial);
            var expected = initial with { UpdatedAt = null };
            (await store.AdvanceAsync(requested)).Should().BeEquivalentTo(expected);
            (await store.GetAsync(initial.PipelineId, initial.SourceId)).Should().BeEquivalentTo(expected);
        }
        finally
        {
            await store.ClearAsync(initial.PipelineId, initial.SourceId);
        }
    }

    private static SourceWatermark Mark(string pipeline, string source, DateTimeOffset timestamp) => new()
    {
        PipelineId = pipeline,
        SourceId = source,
        Kind = WatermarkKind.EditTimestamp,
        Value = SourceWatermark.Encode(timestamp),
        UpdatedAt = timestamp
    };

    private static WebAppFixture CreateHost() => new WebAppFixture()
        .WithProductionJobSubstrate()
        .ConfigureWebHost(builder => builder.UseSetting("ConnectionStrings:redis", string.Empty));
}

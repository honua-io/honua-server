// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Caching;

namespace Honua.Core.Tests.Features.Metadata.Caching;

public sealed class IntervalMetadataV2GraphRevisionProbeTests
{
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(2);

    private readonly ManualTimeProvider _time = new();
    private readonly CountingProbe _database = new();

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task GetCurrentEtagAsync_WithinTheInterval_ProbesOnceForTheWholeNode()
    {
        var schedule = new MetadataV2GraphRevisionProbeSchedule(_time);

        (await ReadProbe(schedule).GetCurrentEtagAsync()).Should().Be(CountingProbe.Etag);
        _time.Advance(TimeSpan.FromSeconds(1));
        (await ReadProbe(schedule).GetCurrentEtagAsync()).Should().BeNull("this node probed within the interval");

        _database.Probes.Should().Be(1);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task GetCurrentEtagAsync_AfterTheInterval_ProbesAgain()
    {
        var schedule = new MetadataV2GraphRevisionProbeSchedule(_time);
        await ReadProbe(schedule).GetCurrentEtagAsync();

        _time.Advance(_interval);

        (await ReadProbe(schedule).GetCurrentEtagAsync()).Should().Be(CountingProbe.Etag);
        _database.Probes.Should().Be(2);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task GetCurrentEtagAsync_WhenTheCurrentRevisionIsRequired_AlwaysProbes()
    {
        var schedule = new MetadataV2GraphRevisionProbeSchedule(_time);
        await ReadProbe(schedule).GetCurrentEtagAsync();
        var sensitive = new IntervalMetadataV2GraphRevisionProbe(
            _database, schedule, new MetadataV2GraphRevalidation(_interval, RequireCurrentRevision: true));

        (await sensitive.GetCurrentEtagAsync()).Should().Be(CountingProbe.Etag);
        (await sensitive.GetCurrentEtagAsync()).Should().Be(CountingProbe.Etag);

        _database.Probes.Should().Be(3);
        (await ReadProbe(schedule).GetCurrentEtagAsync()).Should().BeNull("the sensitive request just probed for the node");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task GetCurrentEtagAsync_ConcurrentReadsWhenAProbeIsDue_ProbeOnce()
    {
        var schedule = new MetadataV2GraphRevisionProbeSchedule(_time);

        var answers = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ =>
            Task.Run(async () => await ReadProbe(schedule).GetCurrentEtagAsync())));

        answers.Count(etag => etag is not null).Should().Be(1);
        _database.Probes.Should().Be(1);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task GetCurrentEtagAsync_WithAZeroInterval_ProbesEveryRead()
    {
        var schedule = new MetadataV2GraphRevisionProbeSchedule(_time);
        var everyRead = new IntervalMetadataV2GraphRevisionProbe(
            _database, schedule, new MetadataV2GraphRevalidation(TimeSpan.Zero, RequireCurrentRevision: false));

        await everyRead.GetCurrentEtagAsync();
        await everyRead.GetCurrentEtagAsync();

        _database.Probes.Should().Be(2);
    }

    private IntervalMetadataV2GraphRevisionProbe ReadProbe(MetadataV2GraphRevisionProbeSchedule schedule)
        => new(_database, schedule, new MetadataV2GraphRevalidation(_interval, RequireCurrentRevision: false));

    private sealed class CountingProbe : IMetadataV2GraphRevisionProbe
    {
        public const string Etag = "etag-7";

        private int _probes;

        public int Probes => _probes;

        public ValueTask<string?> GetCurrentEtagAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _probes);
            return ValueTask.FromResult<string?>(Etag);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _elapsedTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _elapsedTicks);

        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _elapsedTicks, elapsed.Ticks);
    }
}

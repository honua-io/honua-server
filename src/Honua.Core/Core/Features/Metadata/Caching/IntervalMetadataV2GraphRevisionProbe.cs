// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;

namespace Honua.Core.Features.Metadata.Caching;

// One per process: when this node last asked the database which catalog revision is current.
public sealed class MetadataV2GraphRevisionProbeSchedule(TimeProvider timeProvider)
{
    private const long NeverProbed = long.MinValue;

    private long _lastProbeTimestamp = NeverProbed;

    // True for exactly one caller per interval; that caller probes and the others use the snapshot.
    public bool TryClaimProbe(TimeSpan interval)
    {
        var now = timeProvider.GetTimestamp();
        var last = Interlocked.Read(ref _lastProbeTimestamp);
        var due = last == NeverProbed || timeProvider.GetElapsedTime(last, now) >= interval;
        return due && Interlocked.CompareExchange(ref _lastProbeTimestamp, now, last) == last;
    }

    public void RecordProbe() => Interlocked.Exchange(ref _lastProbeTimestamp, timeProvider.GetTimestamp());
}

public sealed record MetadataV2GraphRevalidation(TimeSpan Interval, bool RequireCurrentRevision);

// Answers "no newer revision known" (null) without a query when this node probed within the
// interval, so a read costs at most one probe per interval per node. A caller that requires the
// current revision always probes.
public sealed class IntervalMetadataV2GraphRevisionProbe(
    IMetadataV2GraphRevisionProbe inner,
    MetadataV2GraphRevisionProbeSchedule schedule,
    MetadataV2GraphRevalidation revalidation) : IMetadataV2GraphRevisionProbe
{
    public async ValueTask<string?> GetCurrentEtagAsync(CancellationToken cancellationToken = default)
    {
        if (revalidation.RequireCurrentRevision)
        {
            var current = await inner.GetCurrentEtagAsync(cancellationToken).ConfigureAwait(false);
            schedule.RecordProbe();
            return current;
        }

        return schedule.TryClaimProbe(revalidation.Interval)
            ? await inner.GetCurrentEtagAsync(cancellationToken).ConfigureAwait(false)
            : null;
    }
}

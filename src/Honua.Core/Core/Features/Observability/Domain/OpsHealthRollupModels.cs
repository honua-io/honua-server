// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Observability.Domain;

public enum OpsHealthRollupTier
{
    OneMinute = 0,

    FiveMinute = 1,

    Hourly = 2,
}

public sealed record OpsHealthLatencyPoint
{
    public required string Protocol { get; init; }

    public required long RequestCount { get; init; }

    public required long ErrorCount { get; init; }

    public required double P50Ms { get; init; }

    public required double P95Ms { get; init; }

    public required double P99Ms { get; init; }

    public required double MaxMs { get; init; }

    public LatencyDistribution? Distribution { get; init; }

    public double ErrorRate => RequestCount > 0 ? (double)ErrorCount / RequestCount : 0d;
}

public sealed record OpsHealthVitalsPoint
{
    private static readonly IReadOnlyDictionary<string, int> EmptyBreakdown =
        new Dictionary<string, int>(capacity: 0);

    public required string OverallStatus { get; init; }

    public required int GpQueueTotal { get; init; }

    public IReadOnlyDictionary<string, int> GpQueueBreakdown { get; init; } = EmptyBreakdown;

    public long? AlertPending { get; init; }

    public long? AlertDeadLettered { get; init; }

    public double? DbPoolUtilization { get; init; }

    public required int DbActiveConnections { get; init; }

    public required double CacheHitRatio { get; init; }

    public required double ErrorRate { get; init; }
}

public sealed record OpsHealthRollupSample
{
    public required string ReplicaId { get; init; }

    public required DateTimeOffset CapturedAt { get; init; }

    public required IReadOnlyList<OpsHealthLatencyPoint> Latency { get; init; }

    public required OpsHealthVitalsPoint Vitals { get; init; }
}

public sealed record OpsHealthLatencyRow
{
    public required string ReplicaId { get; init; }

    public required DateTimeOffset BucketStart { get; init; }

    public required OpsHealthLatencyPoint Point { get; init; }
}

public sealed record OpsHealthVitalsRow
{
    public required string ReplicaId { get; init; }

    public required DateTimeOffset BucketStart { get; init; }

    public required OpsHealthVitalsPoint Point { get; init; }
}

public sealed record OpsHealthRollupRetentionPolicy
{
    public required TimeSpan OneMinuteRetention { get; init; }

    public required TimeSpan FiveMinuteRetention { get; init; }

    public required TimeSpan HourlyRetention { get; init; }

    public static OpsHealthRollupRetentionPolicy Default { get; } = new()
    {
        OneMinuteRetention = TimeSpan.FromHours(24),
        FiveMinuteRetention = TimeSpan.FromDays(14),
        HourlyRetention = TimeSpan.FromDays(90),
    };

    public TimeSpan RetentionFor(OpsHealthRollupTier tier) => tier switch
    {
        OpsHealthRollupTier.OneMinute => OneMinuteRetention,
        OpsHealthRollupTier.FiveMinute => FiveMinuteRetention,
        OpsHealthRollupTier.Hourly => HourlyRetention,
        _ => OneMinuteRetention,
    };
}

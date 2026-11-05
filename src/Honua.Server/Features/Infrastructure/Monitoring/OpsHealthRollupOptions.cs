// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Honua.Core.Features.Observability.Domain;

namespace Honua.Infrastructure.Monitoring;

public sealed class OpsHealthRollupOptions
{
    public const string SectionName = "Observability:OpsHealthRollup";

    public bool Enabled { get; set; } = true;

    [Range(5, 3600)]
    public int FlushIntervalSeconds { get; set; } = 45;

    [Range(30, 86_400)]
    public int MaintenanceIntervalSeconds { get; set; } = 300;

    [Range(30, 3600)]
    public int LiveMergeRecencyWindowSeconds { get; set; } = 120;

    [Range(1, 168)]
    public int OneMinuteRetentionHours { get; set; } = 24;

    [Range(1, 90)]
    public int FiveMinuteRetentionDays { get; set; } = 14;

    [Range(1, 730)]
    public int HourlyRetentionDays { get; set; } = 90;

    public string? ReplicaId { get; set; }

    public OpsHealthRollupRetentionPolicy ToRetentionPolicy() => new()
    {
        OneMinuteRetention = TimeSpan.FromHours(OneMinuteRetentionHours),
        FiveMinuteRetention = TimeSpan.FromDays(FiveMinuteRetentionDays),
        HourlyRetention = TimeSpan.FromDays(HourlyRetentionDays),
    };

    public string ResolveReplicaId()
        => string.IsNullOrWhiteSpace(ReplicaId)
            ? string.Create(CultureInfo.InvariantCulture, $"{Environment.MachineName}-{Environment.ProcessId}")
            : ReplicaId.Trim();
}

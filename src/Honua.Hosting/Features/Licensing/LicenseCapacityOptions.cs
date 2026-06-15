// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Licensing;

internal sealed class LicenseCapacityOptions
{
    public const string SectionName = "Licensing:Capacity";

    public bool RegistrationEnabled { get; set; } = true;

    public string? InstanceId { get; set; }

    public string DeploymentRole { get; set; } = "Production";

    public string Topology { get; set; } = "SingleNode";

    public decimal? ServingUnits { get; set; }

    public double? Vcpu { get; set; }

    public double? MemoryGiB { get; set; }

    public int? ServerlessConcurrentExecutions { get; set; }

    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan HeartbeatTtl { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan SampleInterval { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan SampleWindow { get; set; } = TimeSpan.FromDays(30);

    public string RedisKeyPrefix { get; set; } = "honua:license:capacity";
}

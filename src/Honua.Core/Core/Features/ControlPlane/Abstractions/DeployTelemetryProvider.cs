// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.ControlPlane;

public sealed record DeployTelemetryConnectionDescriptor
{
    public required string ConnectionId { get; init; }

    public required string Provider { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public string QueryPath { get; init; } = "/api/v1/query";

    public string? AuthHeaderName { get; init; }

    public string? AuthHeaderValue { get; init; }

    public string? Region { get; init; }

    public int TimeoutSeconds { get; init; } = 10;

    public bool AllowPrivateNetworks { get; init; }
}

public sealed record DeployTelemetryPolicyDescriptor
{
    public required string ConnectionId { get; init; }

    public string? ErrorRateQuery { get; init; }

    public string? ErrorCountQuery { get; init; }

    public double? ErrorRateThreshold { get; init; }

    public string? LatencyP95Query { get; init; }

    public double? LatencyP95ThresholdMs { get; init; }

    public string? MinimumSampleQuery { get; init; }

    public double? MinimumSampleCount { get; init; }

    public bool HasExplicitQueryOverride { get; init; }

    public TimeSpan? MaximumEvidenceStaleness { get; init; }
}

public sealed record DeployTelemetryReadings
{
    public double? SampleCount { get; init; }

    public double? ErrorRate { get; init; }

    public double? ErrorCount { get; init; }

    public bool ErrorCountSeriesAbsent { get; init; }

    public double? LatencyP95 { get; init; }
}

public interface IDeployTelemetryProviderEvaluator
{
    string Provider { get; }

    Task<DeployTelemetryReadings> ReadAsync(
        DeployTelemetryPolicyDescriptor policy,
        DeployTelemetryConnectionDescriptor connection,
        CancellationToken cancellationToken);
}

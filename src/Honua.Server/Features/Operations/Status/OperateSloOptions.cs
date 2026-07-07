// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Operations.Status;

/// <summary>
/// Configuration for the minimal, honest v1 SLO / error-budget contract surfaced in the aggregated
/// operate status payload. Bound from the <c>Slo</c> configuration section. When no availability
/// target is configured the status payload reports the SLO block as explicitly <c>not configured</c>
/// rather than inventing a number.
/// </summary>
/// <remarks>
/// v1 deliberately does NOT stand up a metrics database. The availability SLO is evaluated from the
/// telemetry the server already aggregates in-process: the GIS-protocol-partitioned serving-latency
/// rolling window (<c>HonuaTelemetry.GetServingLatencySnapshot()</c>), which counts requests and
/// server errors (HTTP status &gt;= 500) over its window. The window used for evaluation is therefore
/// that aggregator's window; <see cref="AvailabilityOptions.RollingWindowSeconds"/> is advisory
/// metadata echoed on the payload so operators can see the intended horizon.
/// </remarks>
internal sealed class OperateSloOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Slo";

    /// <summary>
    /// The availability SLO block. Present-but-untargeted (a null <see cref="AvailabilityOptions.Target"/>)
    /// is treated as "not configured".
    /// </summary>
    public AvailabilityOptions Availability { get; set; } = new();

    /// <summary>
    /// Whether a usable availability target is configured (a value in the open interval (0, 1)).
    /// </summary>
    public bool HasAvailabilityTarget
        => Availability.Target is > 0 and < 1;
}

/// <summary>
/// Availability SLO target and rolling-window metadata.
/// </summary>
internal sealed class AvailabilityOptions
{
    /// <summary>
    /// The target availability as a fraction in the open interval (0, 1) — for example <c>0.995</c>
    /// for "99.5% of requests succeed". Null (the default) means the availability SLO is not
    /// configured and the status payload reports it as such.
    /// </summary>
    [Range(0d, 1d)]
    public double? Target { get; set; }

    /// <summary>
    /// Advisory rolling-window horizon in seconds echoed onto the payload. The actual evaluation
    /// window is the in-process serving-latency aggregator's window (default 300s); this value
    /// documents operator intent and does not resize that aggregator.
    /// </summary>
    [Range(1, 30 * 24 * 60 * 60)]
    public int RollingWindowSeconds { get; set; } = 300;
}

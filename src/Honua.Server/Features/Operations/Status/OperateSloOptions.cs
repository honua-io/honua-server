// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Operations.Status;

/// <summary>
/// Availability-target configuration surfaced for operator context in the aggregated operate status.
/// A target alone does not configure a platform SLO: that requires a distributed query source.
/// </summary>
/// <remarks>
/// The in-process serving-latency reservoir remains visible only as a separately named node-local
/// retained-tail diagnostic. It is bounded, restart-resettable, and HTTP-5xx-only, so neither this
/// target nor <see cref="AvailabilityOptions.RollingWindowSeconds"/> turns it into platform evidence.
/// </remarks>
internal sealed class OperateSloOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Slo";

    /// <summary>
    /// The availability SLO block. Present-but-untargeted (a null <see cref="AvailabilityOptions.Target"/>)
    /// is treated as absent operator intent.
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
    /// configured. A non-null value is context only until a distributed SLO source is configured.
    /// </summary>
    [Range(0d, 1d)]
    public double? Target { get; set; }

    /// <summary>
    /// Advisory intended rolling-window horizon in seconds echoed onto the diagnostic. This value
    /// does not resize the in-process reservoir or establish a platform SLO.
    /// </summary>
    [Range(1, 30 * 24 * 60 * 60)]
    public int RollingWindowSeconds { get; set; } = 300;
}

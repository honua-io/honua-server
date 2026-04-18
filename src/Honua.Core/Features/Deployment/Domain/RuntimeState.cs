// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Deployment.Domain;

/// <summary>
/// Observed runtime state of a deployment's serving surface. Runtime state is
/// refreshed by the deployment runtime inspector and is separate from the deployment's
/// lifecycle status; a deployment can be <see cref="DeploymentStatus.Active"/> while its
/// runtime reports a <see cref="RuntimeHealth.Degraded"/> health state.
/// </summary>
public sealed record RuntimeState
{
    /// <summary>
    /// Observed runtime health.
    /// </summary>
    public RuntimeHealth Health { get; init; } = RuntimeHealth.Unknown;

    /// <summary>
    /// Publicly routable URL for the deployment when one is exposed.
    /// </summary>
    public string? PublicUrl { get; init; }

    /// <summary>
    /// When the runtime was most recently observed.
    /// </summary>
    public DateTimeOffset? LastObservedAt { get; init; }

    /// <summary>
    /// When the most recent health probe completed.
    /// </summary>
    public DateTimeOffset? LastHealthCheckAt { get; init; }

    /// <summary>
    /// Free-form operator message describing the current runtime condition.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Non-fatal warnings emitted by the inspector.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Returns an unknown runtime state.
    /// </summary>
    public static RuntimeState Unknown() => new();

    /// <summary>
    /// Returns a healthy runtime state with the given public URL and observation time.
    /// </summary>
    public static RuntimeState Healthy(string? publicUrl = null, DateTimeOffset? observedAt = null)
    {
        var now = observedAt ?? DateTimeOffset.UtcNow;
        return new RuntimeState
        {
            Health = RuntimeHealth.Healthy,
            PublicUrl = publicUrl,
            LastObservedAt = now,
            LastHealthCheckAt = now
        };
    }

    /// <summary>
    /// Returns a degraded runtime state with an operator-facing message.
    /// </summary>
    public static RuntimeState Degraded(string message, string? publicUrl = null, DateTimeOffset? observedAt = null)
    {
        var now = observedAt ?? DateTimeOffset.UtcNow;
        return new RuntimeState
        {
            Health = RuntimeHealth.Degraded,
            PublicUrl = publicUrl,
            LastObservedAt = now,
            LastHealthCheckAt = now,
            Message = message
        };
    }

    /// <summary>
    /// Returns an unhealthy runtime state with an operator-facing message.
    /// </summary>
    public static RuntimeState Unhealthy(string message, string? publicUrl = null, DateTimeOffset? observedAt = null)
    {
        var now = observedAt ?? DateTimeOffset.UtcNow;
        return new RuntimeState
        {
            Health = RuntimeHealth.Unhealthy,
            PublicUrl = publicUrl,
            LastObservedAt = now,
            LastHealthCheckAt = now,
            Message = message
        };
    }
}

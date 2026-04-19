// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Deployment.Domain;

/// <summary>
/// Publication window governing when a deployment becomes visible on its serving
/// surface. Schedules are evaluated deterministically against wall-clock time; they do
/// not imply backend-specific cron semantics.
/// </summary>
public sealed record DeploymentSchedule
{
    /// <summary>
    /// Earliest time the deployment may publish. Null means the deployment publishes as
    /// soon as provisioning completes.
    /// </summary>
    public DateTimeOffset? PublishAt { get; init; }

    /// <summary>
    /// Time at which the deployment auto-retires. Null means the deployment remains
    /// active until explicitly superseded or retired.
    /// </summary>
    public DateTimeOffset? UnpublishAt { get; init; }

    /// <summary>
    /// Returns a schedule that publishes immediately and never auto-retires.
    /// </summary>
    public static DeploymentSchedule Immediate() => new();

    /// <summary>
    /// Returns a schedule that publishes at a specific future time.
    /// </summary>
    public static DeploymentSchedule At(DateTimeOffset publishAt)
        => new() { PublishAt = publishAt };

    /// <summary>
    /// Returns a time-bounded schedule that publishes at <paramref name="publishAt"/> and
    /// auto-retires at <paramref name="unpublishAt"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="unpublishAt"/> is not strictly after <paramref name="publishAt"/>.</exception>
    public static DeploymentSchedule Window(DateTimeOffset publishAt, DateTimeOffset unpublishAt)
    {
        if (unpublishAt <= publishAt)
        {
            throw new ArgumentException(
                "Schedule window must end strictly after it begins.",
                nameof(unpublishAt));
        }

        return new DeploymentSchedule { PublishAt = publishAt, UnpublishAt = unpublishAt };
    }

    /// <summary>
    /// Returns true when the deployment is eligible to publish at the given time.
    /// </summary>
    public bool IsDue(DateTimeOffset asOf)
        => PublishAt is null || asOf >= PublishAt.Value;

    /// <summary>
    /// Returns true when the schedule has expired at the given time.
    /// </summary>
    public bool IsExpired(DateTimeOffset asOf)
        => UnpublishAt is not null && asOf >= UnpublishAt.Value;
}

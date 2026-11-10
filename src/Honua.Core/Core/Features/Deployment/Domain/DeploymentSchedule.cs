// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Deployment.Domain;

// A schedule is evaluated against wall-clock time and does not imply backend cron semantics.
public sealed record DeploymentSchedule
{
    public DateTimeOffset? PublishAt { get; init; }

    public DateTimeOffset? UnpublishAt { get; init; }

    public static DeploymentSchedule Immediate() => new();

    public static DeploymentSchedule At(DateTimeOffset publishAt)
        => new() { PublishAt = publishAt };

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

    public bool IsDue(DateTimeOffset asOf)
        => PublishAt is null || asOf >= PublishAt.Value;

    public bool IsExpired(DateTimeOffset asOf)
        => UnpublishAt is not null && asOf >= UnpublishAt.Value;
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Observability.Domain;

public enum OpsIncidentEpisodeState
{
    Active,
    Recovered,
    Unknown
}

public sealed record OpsIncidentEpisodeSignal
{
    public required string AuthorityScope { get; init; }
    public required string FindingId { get; init; }
    public required string ObservationId { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public required OpsIncidentEpisodeState State { get; init; }
}

public sealed record OpsIncidentEpisode
{
    public required string AuthorityScope { get; init; }
    public required Guid EpisodeId { get; init; }
    public required string FindingId { get; init; }
    public required long LifecycleVersion { get; init; }
    public required OpsIncidentEpisodeState State { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
}

public sealed record OpsIncidentEpisodeIdentity(string AuthorityScope, Guid EpisodeId);

public sealed record OpsIncidentEpisodeReference(string AuthorityScope, Guid EpisodeId, long LifecycleVersion);

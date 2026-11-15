// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Observability.Domain;

public enum OpsIncidentObservationState
{
    Active,
    Recovered,
    Unknown
}

public sealed record OpsIncidentObservation
{
    public required string AuthorityScope { get; init; }
    public required string SourceId { get; init; }
    public required string ObservationId { get; init; }
    public required string FindingId { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public required OpsIncidentObservationState State { get; init; }
    public required string EvidenceJson { get; init; }
}

public sealed record OpsIncidentObservationQuery(
    string AuthorityScope,
    DateTimeOffset Since,
    int Limit = 100)
{
    public string? FindingId { get; init; }
    public DateTimeOffset? Until { get; init; }
}

public sealed record OpsIncidentObservationPage(IReadOnlyList<OpsIncidentObservation> Observations)
{
    public bool Truncated { get; init; }
    public long TotalCount { get; init; }
}

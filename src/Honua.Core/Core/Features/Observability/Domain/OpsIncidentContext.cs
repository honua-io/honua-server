// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Observability.Domain;

public enum OpsIncidentEvidenceAvailability
{
    Missing,
    Available,
    Truncated
}

public sealed record OpsIncidentContext(
    OpsIncidentEpisode Episode,
    OpsIncidentObservationPage Evidence,
    OpsIncidentEvidenceAvailability EvidenceAvailability);

public sealed record OpsIncidentContextQuery(OpsIncidentEpisodeIdentity Identity, int EvidenceLimit = 100);

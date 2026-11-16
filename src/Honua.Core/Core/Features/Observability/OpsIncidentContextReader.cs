// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;

namespace Honua.Core.Features.Observability;

internal sealed class OpsIncidentContextReader(
    IOpsIncidentEpisodeStore episodes,
    IOpsIncidentObservationStore observations) : IOpsIncidentContextReader
{
    public async Task<OpsIncidentContext?> ReadAsync(
        OpsIncidentContextQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.Identity);
        if (query.EvidenceLimit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "Incident evidence limits must be between 1 and 1000.");
        }
        var episode = await episodes.ReadAsync(query.Identity, cancellationToken).ConfigureAwait(false);
        if (episode is null)
        {
            return null;
        }
        var evidence = await observations.ReadAsync(new(episode.AuthorityScope, episode.StartedAt, query.EvidenceLimit)
        {
            FindingId = episode.FindingId,
            Until = episode.ObservedAt
        }, cancellationToken).ConfigureAwait(false);
        var availability = evidence.Observations.Count == 0
            ? OpsIncidentEvidenceAvailability.Missing
            : evidence.Truncated ? OpsIncidentEvidenceAvailability.Truncated : OpsIncidentEvidenceAvailability.Available;
        return new(episode, evidence, availability);
    }
}

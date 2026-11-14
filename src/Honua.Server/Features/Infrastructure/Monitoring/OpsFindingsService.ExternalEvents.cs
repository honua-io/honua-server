// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Observability.Domain;

namespace Honua.Infrastructure.Monitoring;

internal sealed partial class OpsFindingsService
{
    private async Task<EvidenceSourceEnvelope?> EvaluateExternalEventsAsync(
        DateTimeOffset now, List<OpsFinding> findings, CancellationToken cancellationToken)
    {
        if (_externalEventStore is null)
        {
            return null;
        }

        var collection = new OpsFindingsStoreCollection(
            EvidencePostureVocabulary.SourceIds.FindingsExternalEvents,
            EvidencePostureVocabulary.BackendKinds.DurableStore,
            "postgres-ops-external-events", _clock);
        var read = await collection.ReadAsync("recent-events",
            token => _externalEventStore.ReadRecentAsync(now - TimeSpan.FromDays(14), token), cancellationToken).ConfigureAwait(false);
        if (read.Succeeded && read.Value is not null)
        {
            findings.AddRange(OpsExternalEventMapper.MapRecent(read.Value, now));
        }

        return collection.Publish(_collectionLedger, SignalValidity);
    }
}

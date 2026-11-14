// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Observability.Domain;

namespace Honua.Infrastructure.Monitoring;

internal sealed partial class OpsFindingsService
{
    private readonly ICatalogActivationWatchStore? _catalogActivationWatches;
    internal const string CatalogRollbackSourceId = "honua_ops_findings.catalog_rollbacks";
    private const string CatalogRollbackBackendId = "catalog-activation-watch-store";

    private async Task<EvidenceSourceEnvelope> EvaluateCatalogRollbacksAsync(List<OpsFinding> findings, CancellationToken cancellationToken)
    {
        if (_catalogActivationWatches is null)
        {
            return EvidencePostureFactory.NotConfigured(CatalogRollbackSourceId,
                EvidencePostureVocabulary.BackendKinds.DurableStore, CatalogRollbackBackendId);
        }

        IReadOnlyList<CatalogActivationWatch> watches;
        try
        {
            watches = await _catalogActivationWatches.ReadRecentRollbacksAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            return EvidencePostureFactory.Unavailable(CatalogRollbackSourceId,
                EvidencePostureVocabulary.BackendKinds.DurableStore, CatalogRollbackBackendId,
                EvidencePostureVocabulary.ReasonCodes.SourceUnavailable, maximumAge: SignalValidity);
        }

        foreach (var watch in watches)
        {
            const string rule = "catalog-activation-rolled-back";
            var subject = new OpsFindingSubject { OperationId = watch.ChangeId };
            findings.Add(new OpsFinding
            {
                Id = OpsFindingId.Create(rule, subject),
                Rule = rule,
                Severity = OpsFindingSeverity.Warning,
                Title = "Honua undid a catalog change after serving health regressed",
                Explanation = $"Catalog revision {watch.Revision} was undone by revision {watch.RollbackRevision}. {watch.Reason}",
                DetectedAt = watch.ActivatedAt,
                Subject = subject,
                EvidenceRefs = [$"catalog-activation:{watch.Id}", $"admin-change:{watch.ChangeId}"],
            });
        }

        return EvidencePostureFactory.Complete(CatalogRollbackSourceId,
            EvidencePostureVocabulary.BackendKinds.DurableStore, CatalogRollbackBackendId,
            _clock.GetUtcNow(), SignalValidity);
    }
}

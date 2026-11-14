// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Observability.Domain;

namespace Honua.Infrastructure.Monitoring;

internal sealed partial class OpsFindingsService
{
    private readonly ICatalogActivationWatchStore? _catalogActivationWatches;

    private async Task EvaluateCatalogRollbacksAsync(List<OpsFinding> findings, CancellationToken cancellationToken)
    {
        if (_catalogActivationWatches is null)
        {
            return;
        }

        foreach (var watch in await _catalogActivationWatches.ReadRecentRollbacksAsync(cancellationToken).ConfigureAwait(false))
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
    }
}

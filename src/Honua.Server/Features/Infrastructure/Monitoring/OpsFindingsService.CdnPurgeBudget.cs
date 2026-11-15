// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Observability.Domain;

namespace Honua.Infrastructure.Monitoring;

internal sealed partial class OpsFindingsService
{
    private async Task EvaluateCdnPurgeBudgetAsync(
        DateTimeOffset now,
        List<OpsFinding> findings,
        CancellationToken cancellationToken)
    {
        if (_cdnPurgeBudget is null)
        {
            return;
        }

        var month = await _cdnPurgeBudget.ReadCurrentMonthAsync(cancellationToken).ConfigureAwait(false);
        if (month is not { Exceeded: true } spent)
        {
            return;
        }

        var subject = new OpsFindingSubject { TargetId = "cdn" };
        findings.Add(new OpsFinding
        {
            Id = OpsFindingId.Create(RuleCdnPurgeBudget, subject),
            Rule = RuleCdnPurgeBudget,
            Severity = OpsFindingSeverity.Warning,
            Title = "CDN edit purges are paused until next month",
            Explanation =
                $"The monthly edit-purge budget is spent ({spent.SubmittedCount.ToString(CultureInfo.InvariantCulture)} submissions, " +
                $"estimated spend {spent.EstimatedSpend.ToString(CultureInfo.InvariantCulture)}). " +
                "Edited data refreshes when its cache lifetime ends. Access changes and releases still purge. " +
                "Raise SharedCache:PurgeBudget:MonthlyEditSubmissions to resume edit purges.",
            DetectedAt = now,
            Subject = subject,
            EvidenceRefs = ["metric:honua_shared_cache_purge_requests_total"],
        });
    }
}

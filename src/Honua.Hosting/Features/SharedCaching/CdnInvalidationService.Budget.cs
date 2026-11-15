// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Infrastructure.SharedCaching;

internal sealed partial class CdnInvalidationService
{
    // Null means the purge goes ahead: no counter, the cap is off, or the counter could not be read.
    private async Task<CdnInvalidationResult?> RefuseWhenEditBudgetSpentAsync(
        string[] paths,
        CdnPurgeCause cause,
        CancellationToken cancellationToken)
    {
        if (cause.Reason != "edit" || _budget is null)
        {
            return null;
        }

        var budget = _options?.CurrentValue.PurgeBudget ?? new CdnPurgeBudgetOptions();
        if (budget.MonthlyEditSubmissions <= 0)
        {
            return null;
        }

        CdnPurgeAdmission admission;
        try
        {
            admission = await _budget.TryAdmitEditAsync(
                budget.MonthlyEditSubmissions,
                budget.EstimatedCostPerSubmission,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log.PurgeBudgetUnreadable(logger, exception);
            return null;
        }

        if (admission.Decision is not CdnPurgeBudgetDecision.Exceeded)
        {
            return null;
        }

        SharedCacheMetrics.RecordPurge(cause, new CdnPurgeSample(paths, "budget", TimeSpan.Zero));
        if (logger.IsEnabled(LogLevel.Warning))
        {
            Log.PurgeBudgetSpent(
                logger,
                admission.SubmittedCount,
                admission.EstimatedSpend.ToString(CultureInfo.InvariantCulture),
                string.Join(' ', paths));
        }

        return CdnInvalidationResult.BudgetExceeded;
    }
}

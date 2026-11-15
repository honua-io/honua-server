// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Abstractions;

// One counter for edit purges, shared by every task. Access changes and releases do not use it.
public interface ICdnPurgeBudget
{
    Task<CdnPurgeAdmission> TryAdmitEditAsync(
        int monthlyLimit,
        decimal unitCost,
        CancellationToken cancellationToken = default);

    Task<CdnPurgeBudgetMonth?> ReadCurrentMonthAsync(CancellationToken cancellationToken = default);
}

public enum CdnPurgeBudgetDecision
{
    Admitted,
    Exceeded,

    // The counter could not be read. The caller still sends the purge; lifetimes are not the only backstop
    // for a database outage.
    Unavailable,
}

public readonly record struct CdnPurgeAdmission(
    CdnPurgeBudgetDecision Decision,
    int SubmittedCount,
    decimal EstimatedSpend);

public readonly record struct CdnPurgeBudgetMonth(int SubmittedCount, decimal EstimatedSpend, CdnPurgeBudgetDecision Decision);

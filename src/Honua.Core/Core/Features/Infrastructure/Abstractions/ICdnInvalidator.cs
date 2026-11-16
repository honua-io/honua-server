// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Abstractions;

// A failure is returned, never thrown: the caller decides where to report it, and a rollback must not be blocked by it.
public interface ICdnInvalidator
{
    Task<CdnInvalidationResult> InvalidateAsync(
        IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken = default);
}

public enum CdnInvalidationOutcome
{
    NotConfigured,
    Submitted,

    // The CDN has too many invalidations in progress; the same request can succeed shortly.
    Busy,
    Failed,

    // The monthly edit-purge budget is spent. The caller does not retry; cached edits expire with their lifetime.
    BudgetExceeded,
}

public sealed record CdnInvalidationResult(CdnInvalidationOutcome Outcome, string? Detail = null)
{
    public static CdnInvalidationResult NotConfigured { get; } = new(CdnInvalidationOutcome.NotConfigured);

    public static CdnInvalidationResult BudgetExceeded { get; } = new(CdnInvalidationOutcome.BudgetExceeded);

    // A spent budget is not a failed call: the edit stands, and nothing is waiting at the CDN.
    public bool Failed => Outcome is CdnInvalidationOutcome.Failed or CdnInvalidationOutcome.Busy;
}

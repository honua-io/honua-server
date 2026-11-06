// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Security.Admission;

public interface IAiExecutionAdmission
{
    Task<IAiExecutionPermit> AcquireAsync(AiExecutionContext context, CancellationToken cancellationToken);

    Task<AiFinancialPriceBasis?> GetFinancialBasisAsync(AiExecutionContext context, CancellationToken cancellationToken);

    Task<IAiExecutionPermit> AcquireAsync(AiExecutionContext context, AiFinancialRequest request, CancellationToken cancellationToken);
}

public interface IAiExecutionPermit : IAsyncDisposable
{
    CancellationToken Token { get; }

    bool HasFinancialReservation { get; }

    Task RecordFinancialUsageAsync(AiFinancialUsage usage, CancellationToken cancellationToken);

    // The permit is claimed exactly once, and authority is checked again before credential preparation.
    Task BeginInvocationAsync(CancellationToken cancellationToken);

    Task EnsureCurrentAsync(CancellationToken cancellationToken);

    Task AuthorizeSendAsync(CancellationToken cancellationToken);
}

// An admission failure is sanitized and carries no actor, prompt, destination, or credential.
public sealed class AiAdmissionException : Exception
{
    public AiAdmissionException(string code, int retryAfterSeconds = 1)
        : base("AI invocation admission is unavailable or denied.")
    {
        Code = code is "invalid_context" or "invalid_policy" or "uninitialized" or "unavailable"
            or "stopped" or "recovering" or "budget_exceeded" or "concurrency_exceeded"
            or "financial_unsupported" or "financial_basis_invalid" or "financial_basis_expired"
            or "financial_budget_exceeded" or "financial_ledger_full" or "financial_stop"
            or "ledger_full" or "disclosure_denied" or "duplicate_attempt" or "duplicate_invocation" or "invalidated"
            ? code : "unavailable";
        RetryAfterSeconds = Math.Clamp(retryAfterSeconds, 1, AiAdmissionPolicy.MaximumWindowSeconds);
    }

    public string Code { get; }

    public int RetryAfterSeconds { get; }
}

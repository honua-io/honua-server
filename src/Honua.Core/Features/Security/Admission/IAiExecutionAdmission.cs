// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Security.Admission;

/// <summary>Charges and reserves one model invocation against authoritative deployment and actor limits.</summary>
public interface IAiExecutionAdmission
{
    /// <summary>Acquires a single-use permit, or fails closed with a sanitized admission exception.</summary>
    Task<IAiExecutionPermit> AcquireAsync(AiExecutionContext context, CancellationToken cancellationToken);
}

/// <summary>One charged model invocation, owned through complete response/body disposal.</summary>
public interface IAiExecutionPermit : IAsyncDisposable
{
    /// <summary>Caller cancellation linked with authoritative stop, generation/epoch loss and the fixed deadline.</summary>
    CancellationToken Token { get; }

    /// <summary>Claims this permit exactly once and checks current authority before credential preparation.</summary>
    Task BeginInvocationAsync(CancellationToken cancellationToken);

    /// <summary>Rechecks current authority immediately before a model send; this does not charge another attempt.</summary>
    Task EnsureCurrentAsync(CancellationToken cancellationToken);

    /// <summary>Authorizes exactly one actual provider send after invocation start and an authoritative check.</summary>
    Task AuthorizeSendAsync(CancellationToken cancellationToken);
}

/// <summary>Sanitized invocation admission failure without actor, prompt, destination or credential details.</summary>
public sealed class AiAdmissionException : Exception
{
    /// <summary>Creates a bounded, allowlisted admission failure.</summary>
    public AiAdmissionException(string code, int retryAfterSeconds = 1)
        : base("AI invocation admission is unavailable or denied.")
    {
        Code = code is "invalid_context" or "invalid_policy" or "uninitialized" or "unavailable"
            or "stopped" or "recovering" or "budget_exceeded" or "concurrency_exceeded"
            or "ledger_full" or "disclosure_denied" or "duplicate_attempt" or "duplicate_invocation" or "invalidated"
            ? code : "unavailable";
        RetryAfterSeconds = Math.Clamp(retryAfterSeconds, 1, AiAdmissionPolicy.MaximumWindowSeconds);
    }

    /// <summary>Stable failure category; no backend exception message is exposed.</summary>
    public string Code { get; }

    /// <summary>Bounded conservative retry delay, independent of whether the caller retries.</summary>
    public int RetryAfterSeconds { get; }
}

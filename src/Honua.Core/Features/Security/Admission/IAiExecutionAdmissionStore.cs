// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Security.Admission;

/// <summary>One selected durable authority for AI attempts, active reservations, policy and stop epochs.</summary>
/// <remarks>No implementation may fall back to volatile state after a durable-store failure.</remarks>
public interface IAiExecutionAdmissionStore
{
    /// <summary>Atomically charges every applicable scope and reserves one invocation, or changes no accounting.</summary>
    /// <remarks>Attempt IDs are generated internally. Ambiguous commit outcomes deny; never retry under a fresh ID.</remarks>
    Task<AiAdmissionDecision> TryAcquireAsync(AiExecutionContext context, Guid attemptId, CancellationToken cancellationToken);

    /// <summary>Atomically reserves attempts, concurrency and all applicable financial scopes under one policy lock.</summary>
    Task<AiAdmissionDecision> TryAcquireAsync(AiExecutionContext context, Guid attemptId, AiFinancialRequest request, CancellationToken cancellationToken);

    /// <summary>Durably claims the exact financial invocation once; ambiguous results never authorize a send.</summary>
    Task<bool> CommitFinancialSendAsync(AiAdmissionReservation reservation, CancellationToken cancellationToken);

    /// <summary>Settles original supported transport usage without requiring current epoch or send authority.</summary>
    Task<string> SettleFinancialAsync(AiAdmissionReservation reservation, AiFinancialUsage usage, CancellationToken cancellationToken);

    /// <summary>Checks authoritative stop/epoch, deadline and release state. Missing or unavailable state returns false.</summary>
    Task<bool> IsCurrentAsync(AiAdmissionReservation reservation, CancellationToken cancellationToken);

    /// <summary>Idempotently releases this exact reservation; its attempt charge remains in rolling history.</summary>
    Task ReleaseAsync(AiAdmissionReservation reservation, CancellationToken cancellationToken);

    /// <summary>Reads authoritative control state; null means uninitialized, never an unlimited policy.</summary>
    Task<AiAdmissionState?> GetStateAsync(string deploymentId, CancellationToken cancellationToken);

    /// <summary>Applies authenticated operator control and retains its result for exact invocation replay.</summary>
    Task<AiAdmissionControlResult> ApplyControlAsync(AiAdmissionControlCommand command, CancellationToken cancellationToken);
}

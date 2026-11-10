// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Security.Admission;

public interface IAiExecutionAdmissionStore
{
    // A charge updates every applicable scope or changes nothing, and an ambiguous commit denies rather than retrying under a fresh id.
    Task<AiAdmissionDecision> TryAcquireAsync(AiExecutionContext context, Guid attemptId, CancellationToken cancellationToken);

    Task<AiAdmissionDecision> TryAcquireAsync(AiExecutionContext context, Guid attemptId, AiFinancialRequest request, CancellationToken cancellationToken);

    // The financial invocation is claimed once, and an ambiguous result never authorizes a send.
    Task<bool> CommitFinancialSendAsync(AiAdmissionReservation reservation, CancellationToken cancellationToken);

    Task<string> SettleFinancialAsync(AiAdmissionReservation reservation, AiFinancialUsage usage, CancellationToken cancellationToken);

    Task<bool> IsCurrentAsync(AiAdmissionReservation reservation, CancellationToken cancellationToken);

    Task ReleaseAsync(AiAdmissionReservation reservation, CancellationToken cancellationToken);

    // A null control state means uninitialized, never an unlimited policy.
    Task<AiAdmissionState?> GetStateAsync(string deploymentId, CancellationToken cancellationToken);

    Task<AiAdmissionControlResult> ApplyControlAsync(AiAdmissionControlCommand command, CancellationToken cancellationToken);
}

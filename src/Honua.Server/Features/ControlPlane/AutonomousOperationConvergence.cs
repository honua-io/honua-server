// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;

namespace Honua.ControlPlane;

// Invoking an actuator is not evidence that the operation converged.
internal enum AutonomousVerificationState
{
    Converged = 0,
    Failed = 1,
    Indeterminate = 2,
}

internal sealed record AutonomousVerificationResult(
    AutonomousVerificationState State,
    string Message);

internal enum AutonomousCompensationState
{
    NotSupported = 0,
    RolledBack = 1,
    Failed = 2,
    Indeterminate = 3,
}

internal sealed record AutonomousCompensationResult(
    AutonomousCompensationState State,
    string Message);

// Verification reads live state and fails closed when that state cannot be determined.
internal interface IAutonomousOperationConvergence
{
    bool CanHandle(OperationGatewayRequest request);

    bool SupportsCompensation(OperationGatewayRequest request);

    Task<AutonomousVerificationResult> VerifyAsync(
        OperationGatewayRequest request,
        string? executionOperationId,
        CancellationToken cancellationToken = default);

    Task<AutonomousCompensationResult> CompensateAsync(
        OperationGatewayRequest request,
        string? executionOperationId,
        CancellationToken cancellationToken = default);
}

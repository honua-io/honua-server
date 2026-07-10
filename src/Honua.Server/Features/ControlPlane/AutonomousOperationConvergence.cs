// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;

namespace Honua.ControlPlane;

/// <summary>
/// Post-action observation state for an autonomous operation. Execution and convergence are
/// deliberately separate: invoking an actuator is never sufficient evidence of self-healing.
/// </summary>
internal enum AutonomousVerificationState
{
    Converged = 0,
    Failed = 1,
    Indeterminate = 2,
}

/// <summary>Sanitized convergence evidence recorded after an autonomous actuator returns.</summary>
internal sealed record AutonomousVerificationResult(
    AutonomousVerificationState State,
    string Message);

/// <summary>Result of attempting to compensate a non-converged autonomous operation.</summary>
internal enum AutonomousCompensationState
{
    NotSupported = 0,
    RolledBack = 1,
    Failed = 2,
    Indeterminate = 3,
}

/// <summary>Sanitized compensation evidence retained with the final autonomy outcome.</summary>
internal sealed record AutonomousCompensationResult(
    AutonomousCompensationState State,
    string Message);

/// <summary>
/// Action-specific convergence and compensation contract used only by the autonomous gateway
/// path. Implementations must make verification read live state and must fail closed when the
/// state cannot be determined.
/// </summary>
internal interface IAutonomousOperationConvergence
{
    /// <summary>Returns true when this implementation owns verification for the request.</summary>
    bool CanHandle(OperationGatewayRequest request);

    /// <summary>Returns true when this action has a safe, bounded compensation operation.</summary>
    bool SupportsCompensation(OperationGatewayRequest request);

    /// <summary>Observes the post-action state and determines whether the fault converged.</summary>
    Task<AutonomousVerificationResult> VerifyAsync(
        OperationGatewayRequest request,
        string? executionOperationId,
        CancellationToken cancellationToken = default);

    /// <summary>Attempts the supported compensation operation after convergence fails.</summary>
    Task<AutonomousCompensationResult> CompensateAsync(
        OperationGatewayRequest request,
        string? executionOperationId,
        CancellationToken cancellationToken = default);
}

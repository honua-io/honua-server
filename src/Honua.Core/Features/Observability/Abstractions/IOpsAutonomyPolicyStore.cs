// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Observability.Domain;

namespace Honua.Core.Features.Observability.Abstractions;

public interface IOpsAutonomyPolicyStore
{
    Task<OpsAutonomyPolicy?> GetPolicyAsync(string rule, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OpsAutonomyPolicySnapshot>> ListPoliciesAsync(CancellationToken cancellationToken = default);

    Task<OpsAutonomyPolicySnapshot> SetPolicyAsync(
        OpsAutonomyPolicy policy,
        string changedBy,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<OpsAutonomySettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task<OpsAutonomySettings> SetSettingsAsync(
        OpsAutonomySettings settings,
        string changedBy,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<OpsAutonomyReservationResult> TryReserveAutoActionAsync(
        OpsAutonomyReservationRequest request,
        CancellationToken cancellationToken = default);

    Task RecordAutoActionOutcomeAsync(
        string reservationId,
        OpsAutonomyActionOutcome outcome,
        string? operationId = null,
        string? message = null,
        CancellationToken cancellationToken = default);

    Task IncrementProposalRaisedAsync(string rule, CancellationToken cancellationToken = default);

    Task RecordProposalResolutionAsync(
        string rule,
        string proposalId,
        OpsAutonomyProposalResolution resolution,
        CancellationToken cancellationToken = default);
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Observability.Domain;

namespace Honua.Core.Features.Observability.Abstractions;

/// <summary>
/// Durable store for per-rule ops-finding autonomy policy, global autonomy settings, and
/// aggregate outcome counters.
/// </summary>
public interface IOpsAutonomyPolicyStore
{
    /// <summary>
    /// Gets the durable policy for a rule, or <c>null</c> when the rule uses configuration/defaults.
    /// </summary>
    /// <param name="rule">Rule identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The durable policy, or null.</returns>
    Task<OpsAutonomyPolicy?> GetPolicyAsync(string rule, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists durable policies with their aggregate track records.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The durable policy snapshots.</returns>
    Task<IReadOnlyList<OpsAutonomyPolicySnapshot>> ListPoliciesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts a durable policy and records the actor/reason for audit.
    /// </summary>
    /// <param name="policy">Policy to persist.</param>
    /// <param name="changedBy">Actor changing the policy.</param>
    /// <param name="reason">Optional reason for the change.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted policy snapshot.</returns>
    Task<OpsAutonomyPolicySnapshot> SetPolicyAsync(
        OpsAutonomyPolicy policy,
        string changedBy,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets global autonomy settings.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The current settings.</returns>
    Task<OpsAutonomySettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists global autonomy settings and records the actor/reason for audit.
    /// </summary>
    /// <param name="settings">Settings to persist.</param>
    /// <param name="changedBy">Actor changing the settings.</param>
    /// <param name="reason">Optional reason for the change.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted settings.</returns>
    Task<OpsAutonomySettings> SetSettingsAsync(
        OpsAutonomySettings settings,
        string changedBy,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to reserve one autonomous action under the rule's rolling rate cap.
    /// </summary>
    /// <param name="request">Reservation request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reservation result.</returns>
    Task<OpsAutonomyReservationResult> TryReserveAutoActionAsync(
        OpsAutonomyReservationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the terminal outcome for a previously reserved autonomous action.
    /// </summary>
    /// <param name="reservationId">Reservation identifier.</param>
    /// <param name="outcome">Terminal outcome.</param>
    /// <param name="operationId">Gateway execution operation identifier, when available.</param>
    /// <param name="message">Optional outcome detail.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordAutoActionOutcomeAsync(
        string reservationId,
        OpsAutonomyActionOutcome outcome,
        string? operationId = null,
        string? message = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Increments the proposal-raised track-record counter for a rule.
    /// </summary>
    /// <param name="rule">Rule identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task IncrementProposalRaisedAsync(string rule, CancellationToken cancellationToken = default);
}

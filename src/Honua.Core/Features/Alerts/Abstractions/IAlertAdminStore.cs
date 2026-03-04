// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;

namespace Honua.Core.Features.Alerts.Abstractions;

/// <summary>
/// Administrative persistence operations for alert zones and rules.
/// </summary>
public interface IAlertAdminStore
{
    /// <summary>
    /// Lists zones optionally filtered by service.
    /// </summary>
    /// <param name="serviceId">Optional service identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Matching zones</returns>
    Task<IReadOnlyList<AlertZoneDefinition>> ListZonesAsync(
        string? serviceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a zone by identifier.
    /// </summary>
    /// <param name="zoneId">Zone identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Zone definition when found; otherwise null</returns>
    Task<AlertZoneDefinition?> GetZoneAsync(
        long zoneId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a zone.
    /// </summary>
    /// <param name="zone">Zone definition</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created zone</returns>
    Task<AlertZoneDefinition> CreateZoneAsync(
        AlertZoneDefinition zone,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing zone.
    /// </summary>
    /// <param name="zone">Zone definition</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated zone when found; otherwise null</returns>
    Task<AlertZoneDefinition?> UpdateZoneAsync(
        AlertZoneDefinition zone,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a zone.
    /// </summary>
    /// <param name="zoneId">Zone identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True when deleted</returns>
    Task<bool> DeleteZoneAsync(
        long zoneId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists rules optionally filtered by service and layer.
    /// </summary>
    /// <param name="serviceId">Optional service identifier</param>
    /// <param name="layerId">Optional layer identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Matching rules</returns>
    Task<IReadOnlyList<AlertRuleDefinition>> ListRulesAsync(
        string? serviceId,
        int? layerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a rule.
    /// </summary>
    /// <param name="rule">Rule definition</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created rule</returns>
    Task<AlertRuleDefinition> CreateRuleAsync(
        AlertRuleDefinition rule,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a rule.
    /// </summary>
    /// <param name="rule">Rule definition</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated rule when found; otherwise null</returns>
    Task<AlertRuleDefinition?> UpdateRuleAsync(
        AlertRuleDefinition rule,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a rule.
    /// </summary>
    /// <param name="ruleId">Rule identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True when deleted</returns>
    Task<bool> DeleteRuleAsync(
        long ruleId,
        CancellationToken cancellationToken = default);
}

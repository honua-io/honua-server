// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;

namespace Honua.Core.Features.Alerts.Abstractions;

public interface IAlertAdminStore
{
    Task<IReadOnlyList<AlertZoneDefinition>> ListZonesAsync(
        string? serviceId,
        CancellationToken cancellationToken = default);

    Task<AlertZoneDefinition?> GetZoneAsync(
        long zoneId,
        CancellationToken cancellationToken = default);

    Task<AlertZoneDefinition> CreateZoneAsync(
        AlertZoneDefinition zone,
        CancellationToken cancellationToken = default);

    Task<AlertZoneDefinition?> UpdateZoneAsync(
        AlertZoneDefinition zone,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteZoneAsync(
        long zoneId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AlertRuleDefinition>> ListRulesAsync(
        string? serviceId,
        int? layerId,
        CancellationToken cancellationToken = default);

    Task<AlertRuleDefinition?> GetRuleAsync(
        long ruleId,
        CancellationToken cancellationToken = default);

    Task<AlertRuleDefinition> CreateRuleAsync(
        AlertRuleDefinition rule,
        CancellationToken cancellationToken = default);

    Task<AlertRuleDefinition?> UpdateRuleAsync(
        AlertRuleDefinition rule,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteRuleAsync(
        long ruleId,
        CancellationToken cancellationToken = default);

    Task<AlertRuleHealthSnapshot?> GetRuleHealthAsync(
        long ruleId,
        int recentTriggerLimit,
        CancellationToken cancellationToken = default);
}

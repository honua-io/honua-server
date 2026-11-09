// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Alerts.Abstractions;

public interface IAlertEvaluator
{
    Task<AlertEvaluationResult> EvaluateAsync(
        AlertChange change,
        Feature? feature,
        AlertRuleDefinition rule,
        AlertZoneDefinition? zone,
        AlertStateSnapshot? currentState,
        DateTimeOffset evaluatedAt,
        CancellationToken cancellationToken = default);
}

public interface IAlertEditionPolicy
{
    bool IsRuleAllowed(AlertRuleDefinition rule);

    bool IsTriggerAllowed(AlertTriggerType triggerType);

    bool IsChannelAllowed(AlertChannelType channelType);

    bool IsChannelConfigured(AlertChannelType channelType);

    AlertEditionDenialReason GetEntitlementDenialReason(string entitlementKey);

    AlertEditionDenialReason GetChannelDenialReason(AlertChannelType channelType);
}

public interface ILeaderElectionStrategy
{
    Task<bool> TryAcquireAsync(CancellationToken cancellationToken = default);

    Task ReleaseAsync(CancellationToken cancellationToken = default);

    bool IsLeader { get; }

    // A healthy follower that lost the lease reports false; true means the leadership attempt itself failed.
    bool LastAcquireFaulted { get; }

    string InstanceId { get; }
}

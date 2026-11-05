// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;

namespace Honua.Infrastructure.Monitoring;

// Both halves come from the same pass, so findings cannot be paired with a posture from a different instant.
internal sealed record OpsFindingsEvaluation
{
    public required DateTimeOffset EvaluatedAt { get; init; }

    public required IReadOnlyList<OpsFinding> Findings { get; init; }

    public required EvidencePosture Posture { get; init; }
}

internal interface IOpsFindingsEvidenceSource : IOpsFindingsService
{
    Task<OpsFindingsEvaluation> EvaluateWithEvidenceAsync(CancellationToken cancellationToken = default);
}

// A rule's required sources must be complete and fresh before its action may be routed.
internal static class OpsFindingEvidenceMap
{
    private static readonly IReadOnlyList<string> AlertDispatchSources =
        [EvidencePostureVocabulary.SourceIds.FindingsAlertDispatch];

    private static readonly IReadOnlyList<string> ControlPlaneSources =
        [EvidencePostureVocabulary.SourceIds.FindingsControlPlane];

    private static readonly IReadOnlyList<string> DeployPreflightSources =
        [EvidencePostureVocabulary.SourceIds.FindingsDeployPreflight];

    private static readonly IReadOnlyList<string> GpQueueSources =
        [EvidencePostureVocabulary.SourceIds.FindingsGpQueue];

    private static readonly IReadOnlyList<string> WorkflowSources =
        [EvidencePostureVocabulary.SourceIds.FindingsWorkflowOperations];

    private static readonly IReadOnlyList<string> ServingLatencySources =
        [EvidencePostureVocabulary.SourceIds.FindingsServingLatencyRollup];

    private static readonly IReadOnlyList<string> DatabasePressureSources =
        [EvidencePostureVocabulary.SourceIds.FindingsDatabasePressure];

    private static readonly IReadOnlyList<string> BatchBackendSources =
        [EvidencePostureVocabulary.SourceIds.FindingsBatchBackends];

    private static readonly IReadOnlyList<string> ReleaseDivergenceSources =
    [
        EvidencePostureVocabulary.SourceIds.FindingsControlPlane,
        EvidencePostureVocabulary.SourceIds.FindingsWorkflowOperations,
    ];

    // An unrecognized rule maps to the composite source and fails closed instead of being treated as dependency-free.
    public static IReadOnlyList<string> RequiredSourceIds(string rule) => rule switch
    {
        OpsFindingsService.RuleManagedComposeService => [ManagedComposeObserver.SourceId],
        OpsFindingsService.RuleAlertDispatchBacklog => AlertDispatchSources,
        OpsFindingsService.RuleAlertDispatchChannelFailure => AlertDispatchSources,
        OpsFindingsService.RulePlatformReleaseSkew => ControlPlaneSources,
        OpsFindingsService.RulePendingContractMigrations => DeployPreflightSources,
        OpsFindingsService.RuleGpQueueDepth => GpQueueSources,
        OpsFindingsService.RuleDeployManualIntervention => WorkflowSources,
        OpsFindingsService.RuleLocalBackendSubstrate => BatchBackendSources,
        OpsFindingsService.RuleDbBoundedAdmissionPressure => DatabasePressureSources,
        OpsFindingsService.RuleServingLatencySlo => ServingLatencySources,
        OpsFindingsService.RulePlatformReleaseRuntimeDivergence => ReleaseDivergenceSources,
        _ => [EvidencePostureVocabulary.SourceIds.Findings],
    };

    public static IReadOnlyList<string> RequiredSourceIds(OpsFinding finding)
        => finding.Rule == OpsFindingsService.RulePlatformReleaseRuntimeDivergence
            && finding.EvidenceRefs.Contains(OpsFindingsService.ManagedComposeReleaseEvidence, StringComparer.Ordinal)
            ? [EvidencePostureVocabulary.SourceIds.FindingsControlPlane, ManagedComposeObserver.SourceId]
            : RequiredSourceIds(finding.Rule);

    public static bool TryGetActionableRequiredSources(
        OpsFindingsEvaluation evaluation,
        OpsFinding finding,
        out EvidenceSourceEnvelope[] requiredSources)
    {
        var requiredSourceIds = RequiredSourceIds(finding);
        requiredSources = evaluation.Posture.Sources
            .Where(source => requiredSourceIds.Contains(source.SourceId, StringComparer.Ordinal))
            .ToArray();
        return requiredSources.Length == requiredSourceIds.Count &&
            requiredSources.All(EvidencePostureFactory.IsActionable);
    }
}

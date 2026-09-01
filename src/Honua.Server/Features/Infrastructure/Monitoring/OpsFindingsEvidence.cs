// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// One findings evaluation together with the evidence posture of the telemetry sources it actually
/// read. Both halves come from the same pass so a caller can never pair findings with a posture
/// collected at a different instant.
/// </summary>
internal sealed record OpsFindingsEvaluation
{
    /// <summary>Gets the UTC instant the rules were evaluated (response/evaluation time).</summary>
    public required DateTimeOffset EvaluatedAt { get; init; }

    /// <summary>Gets the active findings, ordered by descending severity.</summary>
    public required IReadOnlyList<OpsFinding> Findings { get; init; }

    /// <summary>Gets the posture of every source the evaluation consumed.</summary>
    public required EvidencePosture Posture { get; init; }
}

/// <summary>
/// Findings engine that also publishes the observation-source posture behind its evaluation, so the
/// REST and MCP read surfaces and the proposal gate all reason over the same measured evidence
/// rather than a response-time approximation of it.
/// </summary>
internal interface IOpsFindingsEvidenceSource : IOpsFindingsService
{
    /// <summary>Evaluates all rules and returns the findings with the evidence posture used.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The findings and the posture of the sources that produced them.</returns>
    Task<OpsFindingsEvaluation> EvaluateWithEvidenceAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Closed, documented mapping from a deterministic finding rule to the source ids that rule read.
/// A rule's required sources must all be complete and fresh before its recommended action may be
/// routed, and the same list is published on the wire so a client can verify the decision.
/// </summary>
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

    /// <summary>
    /// Returns the source ids a rule depends on. An unrecognized rule is deliberately mapped to the
    /// composite findings source, which is complete only when every section is — an unknown rule
    /// therefore fails closed rather than being treated as dependency-free.
    /// </summary>
    /// <param name="rule">The kebab-case rule identifier.</param>
    /// <returns>The stable source ids required to act on the rule's finding.</returns>
    public static IReadOnlyList<string> RequiredSourceIds(string rule) => rule switch
    {
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

    public static bool TryGetActionableRequiredSources(
        OpsFindingsEvaluation evaluation,
        OpsFinding finding,
        out EvidenceSourceEnvelope[] requiredSources)
    {
        var requiredSourceIds = RequiredSourceIds(finding.Rule);
        requiredSources = evaluation.Posture.Sources
            .Where(source => requiredSourceIds.Contains(source.SourceId, StringComparer.Ordinal))
            .ToArray();
        return requiredSources.Length == requiredSourceIds.Count &&
            requiredSources.All(EvidencePostureFactory.IsActionable);
    }
}

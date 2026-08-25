// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Observability.Domain;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Builds the shared evidence-integrity envelope for REST and MCP projections.
/// Adapters provide source facts; this helper owns their common fail-closed mapping.
/// </summary>
internal static class EvidencePostureProjection
{
    private const int DefaultValiditySeconds = 300;

    public static EvidencePostureEnvelope ForFindings(
        DateTimeOffset generatedAt,
        IReadOnlyList<OpsFinding> findings)
    {
        var sources = findings
            .SelectMany(finding => finding.EvidencePosture?.Sources ?? [])
            .GroupBy(source => source.SourceId, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(source => CompletenessRank(source.Completeness))
                .ThenBy(source => source.ObservedAt)
                .First())
            .ToArray();

        if (sources.Length == 0)
        {
            sources =
            [
                EvidencePosture.Source(
                    EvidenceSourceIds.OpsFindings,
                    EvidenceBackendKinds.InProcess,
                    "ops-findings-engine",
                    generatedAt,
                    generatedAt,
                    evaluatedAt: generatedAt),
            ];
        }

        return EvidencePosture.Envelope(generatedAt, sources);
    }

    public static EvidencePostureEnvelope ForAlertEvents(
        DateTimeOffset generatedAt,
        DateTimeOffset? requestedFrom,
        DateTimeOffset? requestedTo,
        int requestedPageSize,
        IReadOnlyList<DateTimeOffset> returnedTimestamps,
        bool hasMore)
    {
        var observedAt = Latest(returnedTimestamps);
        var source = EvidencePosture.Source(
            EvidenceSourceIds.AlertEvents,
            EvidenceBackendKinds.DurableStore,
            "alert-event-store",
            observedAt,
            generatedAt,
            coverage: new EvidenceCoverage
            {
                RequestedFrom = requestedFrom,
                RequestedTo = requestedTo,
                ReturnedFrom = Earliest(returnedTimestamps),
                ReturnedTo = observedAt,
                RequestedPageSize = requestedPageSize,
                ReturnedCount = returnedTimestamps.Count,
                HasMore = hasMore,
                Truncated = hasMore,
            },
            maximumObservationAgeSeconds: DefaultValiditySeconds,
            evaluatedAt: generatedAt);

        return EvidencePosture.Envelope(generatedAt, [source]);
    }

    public static EvidencePostureEnvelope ForOperateEvents(
        DateTimeOffset generatedAt,
        DateTimeOffset? requestedFrom,
        DateTimeOffset? requestedTo,
        int requestedPageSize,
        IReadOnlyList<DateTimeOffset> returnedTimestamps,
        bool partialResult,
        IReadOnlyCollection<string> includedSources,
        IReadOnlyCollection<string> failedSources)
    {
        var observedAt = Latest(returnedTimestamps);
        var reasons = partialResult || failedSources.Count > 0
            ? new[] { EvidenceReasonCodes.PartialResult }
            : Array.Empty<string>();
        var source = EvidencePosture.Source(
            EvidenceSourceIds.OperateEvents,
            EvidenceBackendKinds.Composite,
            "operate-event-feed",
            observedAt,
            generatedAt,
            completeness: partialResult || failedSources.Count > 0
                ? EvidenceCompletenessStatuses.Partial
                : EvidenceCompletenessStatuses.Complete,
            coverage: new EvidenceCoverage
            {
                RequestedFrom = requestedFrom,
                RequestedTo = requestedTo,
                ReturnedFrom = Earliest(returnedTimestamps),
                ReturnedTo = observedAt,
                RequestedPageSize = requestedPageSize,
                ReturnedCount = returnedTimestamps.Count,
                IncludedComponentIds = includedSources.ToArray(),
                ExpectedComponentIds = includedSources.Concat(failedSources).ToArray(),
            },
            maximumObservationAgeSeconds: DefaultValiditySeconds,
            reasonCodes: reasons,
            evaluatedAt: generatedAt);

        return EvidencePosture.Envelope(generatedAt, [source]);
    }

    public static EvidencePostureEnvelope ForPlatformRelease(DateTimeOffset generatedAt)
        => EvidencePosture.Envelope(
            generatedAt,
            [
                EvidencePosture.Source(
                    EvidenceSourceIds.PlatformRelease,
                    EvidenceBackendKinds.Configuration,
                    "control-plane-options",
                    generatedAt,
                    generatedAt,
                    evaluatedAt: generatedAt),
            ]);

    public static EvidencePostureEnvelope ForDeployOperations(
        DateTimeOffset generatedAt,
        int page,
        int pageSize,
        int returnedCount,
        bool hasMore,
        IReadOnlyList<DateTimeOffset> returnedTimestamps)
    {
        var observedAt = Latest(returnedTimestamps);
        var source = EvidencePosture.Source(
            EvidenceSourceIds.DeployOperations,
            EvidenceBackendKinds.DurableStore,
            "workflow-operation-store",
            observedAt,
            generatedAt,
            coverage: new EvidenceCoverage
            {
                RequestedPageSize = pageSize,
                ReturnedCount = returnedCount,
                HasMore = hasMore,
                Truncated = hasMore,
                ReturnedFrom = Earliest(returnedTimestamps),
                ReturnedTo = observedAt,
                IncludedComponentIds = [$"page-{page}"],
                ExpectedComponentIds = [$"page-{page}"],
            },
            maximumObservationAgeSeconds: DefaultValiditySeconds,
            evaluatedAt: generatedAt);

        return EvidencePosture.Envelope(generatedAt, [source]);
    }

    private static DateTimeOffset? Latest(IReadOnlyList<DateTimeOffset> timestamps)
        => timestamps.Count == 0 ? null : timestamps.Max();

    private static DateTimeOffset? Earliest(IReadOnlyList<DateTimeOffset> timestamps)
        => timestamps.Count == 0 ? null : timestamps.Min();

    private static int CompletenessRank(string completeness) => completeness switch
    {
        EvidenceCompletenessStatuses.Unavailable => 0,
        EvidenceCompletenessStatuses.Partial => 1,
        EvidenceCompletenessStatuses.NotConfigured => 2,
        _ => 3,
    };
}

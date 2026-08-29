// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Observability.Domain;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Maps deterministic ops findings onto the admin/MCP wire response shape.
/// </summary>
internal static class OpsFindingResponseMapper
{
    public static OpsFindingView Map(OpsFinding finding) => Map(finding, posture: null);

    /// <summary>
    /// Maps a finding, resolving its required source ids and the observation window those sources
    /// actually covered from the posture of the evaluation that produced it.
    /// </summary>
    /// <param name="finding">The finding to project.</param>
    /// <param name="posture">The evidence posture of the evaluation, when available.</param>
    /// <returns>The wire view.</returns>
    public static OpsFindingView Map(OpsFinding finding, EvidencePosture? posture)
    {
        ArgumentNullException.ThrowIfNull(finding);

        var requiredSourceIds = OpsFindingEvidenceMap.RequiredSourceIds(finding.Rule);
        return new OpsFindingView
        {
            RequiredSourceIds = requiredSourceIds,
            ObservationWindow = BuildObservationWindow(requiredSourceIds, posture),
            Id = finding.Id,
            Rule = finding.Rule,
            Severity = finding.Severity.ToString(),
            Title = finding.Title,
            Explanation = finding.Explanation,
            DetectedAt = finding.DetectedAt,
            Subject = new OpsFindingSubjectView
            {
                TargetId = finding.Subject.TargetId,
                WorkloadId = finding.Subject.WorkloadId,
                Channel = finding.Subject.Channel,
                OperationId = finding.Subject.OperationId,
                ReleaseVersion = finding.Subject.ReleaseVersion,
                Protocol = finding.Subject.Protocol,
            },
            EvidenceRefs = finding.EvidenceRefs,
            RecommendedAction = finding.RecommendedAction is null
                ? null
                : new OpsFindingActionView
                {
                    Kind = finding.RecommendedAction.Kind.ToString(),
                    Summary = finding.RecommendedAction.Summary,
                    Reason = finding.RecommendedAction.Reason,
                    AutoSafe = finding.RecommendedAction.AutoSafe,
                    BlastRadius = Math.Max(1, finding.RecommendedAction.BlastRadius),
                },
        };
    }

    /// <summary>
    /// Derives the inclusive interval the finding's required sources observed. It is built only
    /// from measured source timestamps: when the posture is absent, or a required source published
    /// no observation time, the window stays null rather than being approximated from
    /// <c>detectedAt</c>.
    /// </summary>
    private static EvidenceSourceCoverage? BuildObservationWindow(
        IReadOnlyList<string> requiredSourceIds,
        EvidencePosture? posture)
    {
        if (posture is null)
        {
            return null;
        }

        var required = posture.Sources
            .Where(source => requiredSourceIds.Contains(source.SourceId, StringComparer.Ordinal))
            .ToArray();
        if (required.Length != requiredSourceIds.Count)
        {
            return null;
        }

        var observations = required.Select(source => source.ObservedAt).OfType<DateTimeOffset>().ToArray();
        if (observations.Length != required.Length)
        {
            return null;
        }

        // Requested bounds are only published when every required source declared one.
        var requestedFrom = required.Select(source => source.Coverage?.RequestedFrom).OfType<DateTimeOffset>().ToArray();
        var requestedTo = required.Select(source => source.Coverage?.RequestedTo).OfType<DateTimeOffset>().ToArray();

        return new EvidenceSourceCoverage
        {
            RequestedFrom = requestedFrom.Length == required.Length ? requestedFrom.Min() : null,
            RequestedTo = requestedTo.Length == required.Length ? requestedTo.Max() : null,
            ReturnedFrom = observations.Min(),
            ReturnedTo = observations.Max(),
        };
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Observability.Domain;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Maps deterministic ops findings onto the admin/MCP wire response shape.
/// </summary>
internal static class OpsFindingResponseMapper
{
    public static OpsFindingView Map(OpsFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        return new OpsFindingView
        {
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
}

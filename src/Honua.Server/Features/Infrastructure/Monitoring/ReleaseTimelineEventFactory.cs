// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Observability.Domain;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Maps an observed workflow-operation transition onto the normalized <see cref="OperateEventKind.Release"/>
/// timeline event. Shared by the in-process <see cref="ReleaseTimelineTransitionListener"/> (which appends
/// to the <see cref="ReleaseTimelineBuffer"/> so releases show on the Operate timeline) and the realtime
/// broadcaster (#2554, which pushes the same event to the <c>operate-events</c> hub group). Keeping one
/// mapping guarantees a live-pushed release event is identical to the one read back from the timeline API.
/// </summary>
internal static class ReleaseTimelineEventFactory
{
    /// <summary>Builds the release timeline event for a transition.</summary>
    /// <param name="transition">The observed workflow-operation transition.</param>
    /// <returns>The normalized release event.</returns>
    public static OperateEvent Create(WorkflowOperationTransition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);

        var operation = transition.Operation;
        var occurredTicks = transition.OccurredAt.UtcTicks.ToString(CultureInfo.InvariantCulture);

        return new OperateEvent
        {
            EventId = $"release:{operation.OperationId}:{transition.Kind}:{occurredTicks}",
            Kind = OperateEventKind.Release,
            Severity = MapSeverity(transition.Kind),
            OccurredAt = transition.OccurredAt,
            Title = BuildTitle(transition),
            Summary = operation.CurrentPhase,
            Actor = operation.Audit.RequestedBy,
            CorrelationId = transition.CorrelationId,
            OperationId = transition.OperationId,
            ReleaseId = transition.ReleaseId,
            ResourceRef = "release/" + operation.OperationId
        };
    }

    private static string BuildTitle(WorkflowOperationTransition transition)
    {
        var subject = transition.TargetId
            ?? transition.Operation.MetadataRelease?.PackageId
            ?? transition.OperationId;
        var action = transition.Kind switch
        {
            WorkflowOperationTransitionKind.Created => "Deploy operation created",
            WorkflowOperationTransitionKind.Submitted => "Deploy submitted",
            WorkflowOperationTransitionKind.Promoted => "Deploy promoted",
            WorkflowOperationTransitionKind.RolledBack => "Deploy rolled back",
            WorkflowOperationTransitionKind.ManualInterventionRequired => "Deploy needs manual intervention",
            _ => "Deploy transition"
        };

        return $"{action}: {subject}";
    }

    private static OperateEventSeverity MapSeverity(WorkflowOperationTransitionKind kind) => kind switch
    {
        WorkflowOperationTransitionKind.Created => OperateEventSeverity.Info,
        WorkflowOperationTransitionKind.Submitted => OperateEventSeverity.Info,
        WorkflowOperationTransitionKind.Promoted => OperateEventSeverity.Notice,
        WorkflowOperationTransitionKind.RolledBack => OperateEventSeverity.Warning,
        WorkflowOperationTransitionKind.ManualInterventionRequired => OperateEventSeverity.Error,
        _ => OperateEventSeverity.Info
    };
}

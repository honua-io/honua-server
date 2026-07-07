// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Observability.Domain;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// The in-repo consumer of the workflow-transition seam: maps each observed deploy/release transition to
/// an <see cref="OperateEventKind.Release"/> event and appends it to the <see cref="ReleaseTimelineBuffer"/>
/// so it appears on the unified Operate timeline (#2562). The realtime hub (#2554) attaches to the SAME
/// seam as a second, independent listener.
/// </summary>
internal sealed class ReleaseTimelineTransitionListener : IWorkflowOperationTransitionListener
{
    private readonly ReleaseTimelineBuffer _buffer;

    public ReleaseTimelineTransitionListener(ReleaseTimelineBuffer buffer)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
    }

    public Task OnTransitionAsync(WorkflowOperationTransition transition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transition);

        _buffer.Append(MapEvent(transition));
        return Task.CompletedTask;
    }

    private static OperateEvent MapEvent(WorkflowOperationTransition transition)
    {
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

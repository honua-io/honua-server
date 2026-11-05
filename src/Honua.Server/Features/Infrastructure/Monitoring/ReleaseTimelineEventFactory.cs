// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Observability.Domain;

namespace Honua.Infrastructure.Monitoring;

internal static class ReleaseTimelineEventFactory
{
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

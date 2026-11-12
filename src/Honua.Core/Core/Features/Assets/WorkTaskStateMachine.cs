// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Assets;

// The rc.3 task state machine (spec: State machine and concurrency). Terminal tasks never reopen.
public static class WorkTaskStateMachine
{
    public static WorkTaskDecision Decide(WorkTask task, WorkTaskTransitionRequest request, WorkTaskActor actor)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        if (!WorkTaskAction.Transitions.Contains(request.Action ?? string.Empty, StringComparer.Ordinal))
        {
            return WorkTaskDecision.Refuse($"action must be one of: {string.Join(", ", WorkTaskAction.Transitions)}.");
        }

        var action = request.Action!;
        if (CheckPrecondition(task, action, actor) is { } refused)
        {
            return refused;
        }

        return action switch
        {
            WorkTaskAction.Assign => WithAssignee(request, WorkTaskStatus.Assigned),
            WorkTaskAction.Unassign => WorkTaskDecision.Allow(WorkTaskStatus.Unassigned, null),
            WorkTaskAction.Start => WorkTaskDecision.Allow(WorkTaskStatus.InProgress, task.Assignee),
            WorkTaskAction.Reassign => WithAssignee(request, task.Status),
            _ => string.IsNullOrWhiteSpace(request.Reason)
                ? WorkTaskDecision.Refuse("reason is required to cancel a task.")
                : WorkTaskDecision.Allow(WorkTaskStatus.Cancelled, task.Assignee),
        };
    }

    public static IReadOnlyList<string> PermittedActions(WorkTask task, WorkTaskActor actor)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(actor);

        return [.. WorkTaskAction.Transitions.Append(WorkTaskAction.Complete)
            .Where(action => CheckPrecondition(task, action, actor) is null)];
    }

    // Null when the actor may perform the action from the task's current state.
    public static WorkTaskDecision? CheckPrecondition(WorkTask task, string action, WorkTaskActor actor)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(actor);

        var crewAction = action is WorkTaskAction.Start or WorkTaskAction.Complete;
        var permitted = actor.IsPlanner || (crewAction && string.Equals(task.Assignee, actor.Id, StringComparison.Ordinal));
        if (!permitted)
        {
            return WorkTaskDecision.Forbid();
        }

        if (WorkTaskStatus.IsTerminal(task.Status))
        {
            return WorkTaskDecision.Refuse($"A {task.Status} task cannot change; create a follow-up task.");
        }

        return RequiredStatuses(action).Contains(task.Status, StringComparer.Ordinal)
            ? null
            : WorkTaskDecision.Refuse($"A task that is {task.Status} cannot {action}.");
    }

    private static string[] RequiredStatuses(string action) => action switch
    {
        WorkTaskAction.Assign => [WorkTaskStatus.Unassigned],
        WorkTaskAction.Unassign or WorkTaskAction.Start => [WorkTaskStatus.Assigned],
        WorkTaskAction.Reassign => [WorkTaskStatus.Assigned, WorkTaskStatus.InProgress],
        WorkTaskAction.Complete => [WorkTaskStatus.InProgress],
        _ => [WorkTaskStatus.Unassigned, WorkTaskStatus.Assigned, WorkTaskStatus.InProgress],
    };

    private static WorkTaskDecision WithAssignee(WorkTaskTransitionRequest request, string status)
        => string.IsNullOrWhiteSpace(request.Assignee)
            ? WorkTaskDecision.Refuse("assignee is required.")
            : WorkTaskDecision.Allow(status, request.Assignee.Trim());
}

public sealed record WorkTaskDecision(bool Allowed, bool Forbidden, string? NewStatus, string? NewAssignee, string? Reason)
{
    public static WorkTaskDecision Allow(string status, string? assignee) => new(true, false, status, assignee, null);

    public static WorkTaskDecision Refuse(string reason) => new(false, false, null, null, reason);

    public static WorkTaskDecision Forbid() => new(false, true, null, null, "The caller may not perform this action on the task.");
}

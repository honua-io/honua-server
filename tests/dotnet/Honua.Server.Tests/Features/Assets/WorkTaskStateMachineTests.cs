// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Assets;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Assets;

[Protocol(TestProtocols.Admin)]
public sealed class WorkTaskStateMachineTests
{
    private static readonly WorkTaskActor Planner = new("planner", IsPlanner: true);
    private static readonly WorkTaskActor Crew = new("crew-1", IsPlanner: false);
    private static readonly WorkTaskActor OtherCrew = new("crew-2", IsPlanner: false);

    [UnitTheory]
    [InlineData(WorkTaskStatus.Unassigned, WorkTaskAction.Assign, WorkTaskStatus.Assigned)]
    [InlineData(WorkTaskStatus.Assigned, WorkTaskAction.Unassign, WorkTaskStatus.Unassigned)]
    [InlineData(WorkTaskStatus.Assigned, WorkTaskAction.Start, WorkTaskStatus.InProgress)]
    [InlineData(WorkTaskStatus.Assigned, WorkTaskAction.Reassign, WorkTaskStatus.Assigned)]
    [InlineData(WorkTaskStatus.InProgress, WorkTaskAction.Reassign, WorkTaskStatus.InProgress)]
    [InlineData(WorkTaskStatus.Unassigned, WorkTaskAction.Cancel, WorkTaskStatus.Cancelled)]
    [InlineData(WorkTaskStatus.Assigned, WorkTaskAction.Cancel, WorkTaskStatus.Cancelled)]
    [InlineData(WorkTaskStatus.InProgress, WorkTaskAction.Cancel, WorkTaskStatus.Cancelled)]
    public void Decide_AllowedEdge_MovesToTheSpecifiedState(string from, string action, string to)
    {
        var decision = WorkTaskStateMachine.Decide(Task(from), Request(action), Planner);

        decision.Allowed.Should().BeTrue(decision.Reason);
        decision.NewStatus.Should().Be(to);
    }

    [UnitTheory]
    [InlineData(WorkTaskStatus.Unassigned, WorkTaskAction.Unassign)]
    [InlineData(WorkTaskStatus.Unassigned, WorkTaskAction.Start)]
    [InlineData(WorkTaskStatus.Unassigned, WorkTaskAction.Reassign)]
    [InlineData(WorkTaskStatus.Assigned, WorkTaskAction.Assign)]
    [InlineData(WorkTaskStatus.InProgress, WorkTaskAction.Assign)]
    [InlineData(WorkTaskStatus.InProgress, WorkTaskAction.Unassign)]
    [InlineData(WorkTaskStatus.InProgress, WorkTaskAction.Start)]
    [InlineData(WorkTaskStatus.Completed, WorkTaskAction.Cancel)]
    [InlineData(WorkTaskStatus.Completed, WorkTaskAction.Reassign)]
    [InlineData(WorkTaskStatus.Cancelled, WorkTaskAction.Assign)]
    [InlineData(WorkTaskStatus.Cancelled, WorkTaskAction.Start)]
    [InlineData(WorkTaskStatus.Assigned, "complete")]
    [InlineData(WorkTaskStatus.Assigned, "reopen")]
    public void Decide_DisallowedEdge_IsRefused(string from, string action)
    {
        var decision = WorkTaskStateMachine.Decide(Task(from), Request(action), Planner);

        decision.Allowed.Should().BeFalse();
        decision.Forbidden.Should().BeFalse();
    }

    [UnitTheory]
    [InlineData(WorkTaskAction.Assign, WorkTaskStatus.Unassigned)]
    [InlineData(WorkTaskAction.Unassign, WorkTaskStatus.Assigned)]
    [InlineData(WorkTaskAction.Reassign, WorkTaskStatus.Assigned)]
    [InlineData(WorkTaskAction.Cancel, WorkTaskStatus.Assigned)]
    public void Decide_CrewAttemptsPlannerAction_IsForbidden(string action, string from)
    {
        var decision = WorkTaskStateMachine.Decide(Task(from), Request(action), Crew);

        decision.Forbidden.Should().BeTrue();
    }

    [UnitTest]
    public void Decide_StartByAnotherCrewMember_IsForbiddenButAssigneeMayStart()
    {
        var task = Task(WorkTaskStatus.Assigned);

        WorkTaskStateMachine.Decide(task, Request(WorkTaskAction.Start), OtherCrew).Forbidden.Should().BeTrue();
        WorkTaskStateMachine.Decide(task, Request(WorkTaskAction.Start), Crew).Allowed.Should().BeTrue();
    }

    [UnitTest]
    public void Decide_CancelWithoutReasonOrAssignWithoutAssignee_IsRefused()
    {
        WorkTaskStateMachine.Decide(Task(WorkTaskStatus.Assigned), Request(WorkTaskAction.Cancel) with { Reason = " " }, Planner)
            .Allowed.Should().BeFalse();
        WorkTaskStateMachine.Decide(Task(WorkTaskStatus.Unassigned), Request(WorkTaskAction.Assign) with { Assignee = null }, Planner)
            .Allowed.Should().BeFalse();
    }

    [UnitTest]
    public void PermittedActions_ReflectsStateAndRole()
    {
        WorkTaskStateMachine.PermittedActions(Task(WorkTaskStatus.InProgress), Crew)
            .Should().Equal(WorkTaskAction.Complete);
        WorkTaskStateMachine.PermittedActions(Task(WorkTaskStatus.Assigned), Planner)
            .Should().Equal(WorkTaskAction.Unassign, WorkTaskAction.Start, WorkTaskAction.Reassign, WorkTaskAction.Cancel);
        WorkTaskStateMachine.PermittedActions(Task(WorkTaskStatus.Completed), Planner).Should().BeEmpty();
    }

    private static WorkTaskTransitionRequest Request(string action)
        => new() { Action = action, Assignee = "crew-3", Reason = "Duplicate report", ExpectedVersion = 1, CommandId = Guid.NewGuid() };

    private static WorkTask Task(string status) => new()
    {
        TaskId = Guid.NewGuid(),
        ServiceId = "county",
        Type = WorkTaskType.Repair,
        Status = status,
        Version = 1,
        GeometrySource = "explicit",
        Title = "Fix sign",
        Assignee = status is WorkTaskStatus.Unassigned ? null : Crew.Id,
        Form = new WorkTaskFormReference { FormId = "service", Version = 1 },
        CreatedBy = "planner",
    };
}

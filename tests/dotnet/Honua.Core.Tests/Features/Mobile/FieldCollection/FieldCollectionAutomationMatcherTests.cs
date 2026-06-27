// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Mobile.FieldCollection.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Mobile.FieldCollection;

public sealed class FieldCollectionAutomationMatcherTests
{
    private static FieldCollectionAutomationEvent CreateEvent(
        int layerId = 100,
        FieldCollectionChangeOperation operation = FieldCollectionChangeOperation.Insert)
        => new()
        {
            ClientId = "device-1",
            ChangeId = "chg-1",
            FeatureId = "feat-1",
            LayerId = layerId,
            Operation = operation,
            Version = 1,
            Generation = 1,
            Timestamp = DateTimeOffset.UnixEpoch,
        };

    private static FieldCollectionAutomationAction CreateAction(
        string id = "a1",
        bool enabled = true,
        int? layerId = null,
        params FieldCollectionChangeOperation[] operations)
        => new()
        {
            Id = id,
            DisplayName = id,
            ActionType = FieldCollectionAutomationActionType.Webhook,
            Enabled = enabled,
            LayerId = layerId,
            Operations = operations.Length == 0
                ? ImmutableArray<FieldCollectionChangeOperation>.Empty
                : operations.ToImmutableArray(),
        };

    [UnitTest]
    public void Matches_LayerAgnosticAnyOperationEnabled_ReturnsTrue()
    {
        Assert.True(FieldCollectionAutomationMatcher.Matches(CreateAction(), CreateEvent()));
    }

    [UnitTest]
    public void Matches_DisabledAction_ReturnsFalse()
    {
        Assert.False(FieldCollectionAutomationMatcher.Matches(CreateAction(enabled: false), CreateEvent()));
    }

    [UnitTest]
    public void Matches_DifferentLayer_ReturnsFalse()
    {
        var action = CreateAction(layerId: 200);
        Assert.False(FieldCollectionAutomationMatcher.Matches(action, CreateEvent(layerId: 100)));
    }

    [UnitTest]
    public void Matches_SameLayer_ReturnsTrue()
    {
        var action = CreateAction(layerId: 100);
        Assert.True(FieldCollectionAutomationMatcher.Matches(action, CreateEvent(layerId: 100)));
    }

    [UnitTest]
    public void Matches_OperationNotInFilter_ReturnsFalse()
    {
        var action = CreateAction(operations: FieldCollectionChangeOperation.Delete);
        Assert.False(FieldCollectionAutomationMatcher.Matches(
            action,
            CreateEvent(operation: FieldCollectionChangeOperation.Insert)));
    }

    [UnitTest]
    public void Matches_OperationInFilter_ReturnsTrue()
    {
        var action = CreateAction(
            operations: new[] { FieldCollectionChangeOperation.Insert, FieldCollectionChangeOperation.Update });
        Assert.True(FieldCollectionAutomationMatcher.Matches(
            action,
            CreateEvent(operation: FieldCollectionChangeOperation.Update)));
    }

    [UnitTest]
    public void Match_FiltersAndPreservesOrder_AndCreatesStableInvocationIds()
    {
        var actions = new[]
        {
            CreateAction(id: "match-1"),
            CreateAction(id: "wrong-layer", layerId: 999),
            CreateAction(id: "disabled", enabled: false),
            CreateAction(id: "match-2", operations: FieldCollectionChangeOperation.Insert),
        };

        var automationEvent = CreateEvent(operation: FieldCollectionChangeOperation.Insert);
        var invocations = FieldCollectionAutomationMatcher.Match(actions, automationEvent);

        Assert.Equal(2, invocations.Count);
        Assert.Equal("match-1", invocations[0].Action.Id);
        Assert.Equal("match-2", invocations[1].Action.Id);
        Assert.Equal("device-1:chg-1:match-1", invocations[0].InvocationId);
    }

    [UnitTest]
    public void Create_IsDeterministicForSameChangeAndAction()
    {
        var action = CreateAction(id: "a-deterministic");
        var automationEvent = CreateEvent();

        var first = FieldCollectionActionInvocation.Create(action, automationEvent);
        var second = FieldCollectionActionInvocation.Create(action, automationEvent);

        Assert.Equal(first.InvocationId, second.InvocationId);
    }
}

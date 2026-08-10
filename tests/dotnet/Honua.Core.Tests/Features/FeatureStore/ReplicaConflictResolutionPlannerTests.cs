// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;

namespace Honua.Core.Tests.Features.FeatureStore;

/// <summary>
/// Unit tests for <see cref="ReplicaConflictResolutionPlanner"/> (#2430): the decision table that turns
/// an operator-selected resolution action into the feature-store effect that makes it real. The
/// polarity of accept-client vs keep-server depends entirely on whether the conflicting client edit was
/// committed at sync time, which is the invariant these tests pin.
/// </summary>
public sealed class ReplicaConflictResolutionPlannerTests
{
    private static ReplicaConflictRecord Conflict(
        ReplicaConflictType conflictType = ReplicaConflictType.Attribute,
        bool clientEditApplied = false,
        string? clientState = """{"attributes":{"objectid":1,"name":"client"},"geometry":{"x":1.0,"y":2.0}}""",
        string? serverState = """{"attributes":{"objectid":1,"name":"server"},"geometry":{"x":9.0,"y":8.0}}""")
        => new()
        {
            ConflictId = "c1",
            ReplicaId = "r1",
            ServiceId = "svc",
            LayerId = 0,
            ObjectId = 1,
            ConflictType = conflictType,
            Status = ReplicaConflictStatus.Pending,
            ServerGeneration = 5,
            ClientEditApplied = clientEditApplied,
            ClientStateJson = clientState,
            ServerStateJson = serverState,
            DetectedAt = DateTimeOffset.UtcNow,
        };

    private static ReplicaConflictResolutionInputs NoInputs => new(FieldValues: null, GeometrySource: null);

    private static ReplicaConflictResolutionInputs Merge(params (string Field, string RawJson)[] fields)
        => new(
            fields.ToDictionary(
                field => field.Field,
                field => JsonDocument.Parse(field.RawJson).RootElement.Clone(),
                StringComparer.Ordinal),
            GeometrySource: null);

    [Fact]
    public void Plan_AcceptClientWhenClientEditNotApplied_WritesClientState()
    {
        var plan = ReplicaConflictResolutionPlanner.Plan(
            Conflict(), ReplicaConflictResolutionAction.AcceptClient, NoInputs);

        plan.IsAccepted.Should().BeTrue();
        plan.Effect.Should().Be(ReplicaConflictResolutionEffect.WriteFeatureState);
        plan.CommittedNewServerState.Should().BeTrue();
        plan.FeatureStateJson.Should().Contain("client");
    }

    [Fact]
    public void Plan_AcceptClientWhenClientEditAlreadyApplied_IsANoOp()
    {
        var plan = ReplicaConflictResolutionPlanner.Plan(
            Conflict(clientEditApplied: true), ReplicaConflictResolutionAction.AcceptClient, NoInputs);

        plan.IsAccepted.Should().BeTrue();
        plan.Effect.Should().Be(ReplicaConflictResolutionEffect.None);
        plan.CommittedNewServerState.Should().BeFalse(
            "last-write-wins already committed the client edit, so accepting it creates no new state");
    }

    [Fact]
    public void Plan_KeepServerWhenClientEditApplied_RestoresCapturedServerState()
    {
        var plan = ReplicaConflictResolutionPlanner.Plan(
            Conflict(clientEditApplied: true), ReplicaConflictResolutionAction.KeepServer, NoInputs);

        plan.Effect.Should().Be(ReplicaConflictResolutionEffect.WriteFeatureState);
        plan.CommittedNewServerState.Should().BeTrue();
        plan.FeatureStateJson.Should().Contain("server");
    }

    [Fact]
    public void Plan_KeepServerWhenClientEditNotApplied_IsANoOp()
    {
        var plan = ReplicaConflictResolutionPlanner.Plan(
            Conflict(), ReplicaConflictResolutionAction.KeepServer, NoInputs);

        plan.Effect.Should().Be(ReplicaConflictResolutionEffect.None);
        plan.CommittedNewServerState.Should().BeFalse();
    }

    [Fact]
    public void Plan_RejectClientMatchesKeepServer()
    {
        var keepServer = ReplicaConflictResolutionPlanner.Plan(
            Conflict(clientEditApplied: true), ReplicaConflictResolutionAction.KeepServer, NoInputs);
        var rejectClient = ReplicaConflictResolutionPlanner.Plan(
            Conflict(clientEditApplied: true), ReplicaConflictResolutionAction.RejectClient, NoInputs);

        rejectClient.Should().Be(keepServer);
    }

    [Fact]
    public void Plan_AcceptClientOnWithheldDeleteUpdate_DeletesTheFeature()
    {
        var plan = ReplicaConflictResolutionPlanner.Plan(
            Conflict(ReplicaConflictType.DeleteUpdate), ReplicaConflictResolutionAction.AcceptClient, NoInputs);

        plan.Effect.Should().Be(ReplicaConflictResolutionEffect.DeleteFeature);
        plan.CommittedNewServerState.Should().BeTrue();
    }

    [Fact]
    public void Plan_KeepServerAfterCommittedClientDelete_IsRejectedAsNotApplicable()
    {
        var plan = ReplicaConflictResolutionPlanner.Plan(
            Conflict(ReplicaConflictType.DeleteUpdate, clientEditApplied: true),
            ReplicaConflictResolutionAction.KeepServer,
            NoInputs);

        plan.IsAccepted.Should().BeFalse();
        plan.Rejection.Should().Be(ReplicaConflictResolutionRejection.NotApplicable);
        plan.RejectionMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Plan_AcceptClientOnServerDeletedFeature_IsRejectedAsNotApplicable()
    {
        var plan = ReplicaConflictResolutionPlanner.Plan(
            Conflict(ReplicaConflictType.UpdateDelete), ReplicaConflictResolutionAction.AcceptClient, NoInputs);

        plan.Rejection.Should().Be(ReplicaConflictResolutionRejection.NotApplicable);
    }

    [Fact]
    public void Plan_KeepServerOnServerDeletedFeature_IsANoOp()
    {
        var plan = ReplicaConflictResolutionPlanner.Plan(
            Conflict(ReplicaConflictType.UpdateDelete, clientEditApplied: true),
            ReplicaConflictResolutionAction.KeepServer,
            NoInputs);

        plan.IsAccepted.Should().BeTrue();
        plan.Effect.Should().Be(ReplicaConflictResolutionEffect.None);
    }

    [Fact]
    public void Plan_DeferNeverWrites()
    {
        var plan = ReplicaConflictResolutionPlanner.Plan(
            Conflict(clientEditApplied: true), ReplicaConflictResolutionAction.Defer, NoInputs);

        plan.Effect.Should().Be(ReplicaConflictResolutionEffect.None);
        plan.CommittedNewServerState.Should().BeFalse();
    }

    [Fact]
    public void Plan_MergeFieldsWithoutValues_IsRejectedAsInvalidRequest()
    {
        var plan = ReplicaConflictResolutionPlanner.Plan(
            Conflict(), ReplicaConflictResolutionAction.MergeFields, NoInputs);

        plan.Rejection.Should().Be(ReplicaConflictResolutionRejection.InvalidRequest);
    }

    [Fact]
    public void Plan_MergeFieldsOverridesFieldsCaseInsensitivelyAndKeepsGeometry()
    {
        var conflict = Conflict(
            clientEditApplied: true,
            serverState: """{"attributes":{"objectid":1,"NAME":"server","note":"keep"},"geometry":{"x":9.0,"y":8.0}}""",
            clientState: """{"attributes":{"objectid":1,"NAME":"client","note":"keep"},"geometry":{"x":1.0,"y":2.0}}""");

        var plan = ReplicaConflictResolutionPlanner.Plan(
            conflict, ReplicaConflictResolutionAction.MergeFields, Merge(("name", "\"merged\"")));

        plan.IsAccepted.Should().BeTrue();
        plan.Effect.Should().Be(ReplicaConflictResolutionEffect.WriteFeatureState);

        using var document = JsonDocument.Parse(plan.FeatureStateJson!);
        var attributes = document.RootElement.GetProperty("attributes");
        attributes.GetProperty("NAME").GetString().Should().Be(
            "merged", "an operator-supplied 'name' must replace the existing 'NAME' rather than add a duplicate key");
        attributes.TryGetProperty("name", out _).Should().BeFalse();
        attributes.GetProperty("note").GetString().Should().Be("keep");
        document.RootElement.GetProperty("geometry").GetProperty("x").GetDouble().Should().Be(1.0,
            "the merge base is the committed state, which under last-write-wins is the client's");
    }

    [Fact]
    public void Plan_MergeFieldsAddsUnknownFields()
    {
        var plan = ReplicaConflictResolutionPlanner.Plan(
            Conflict(), ReplicaConflictResolutionAction.MergeFields, Merge(("status", "\"reviewed\"")));

        using var document = JsonDocument.Parse(plan.FeatureStateJson!);
        document.RootElement.GetProperty("attributes").GetProperty("status").GetString().Should().Be("reviewed");
    }

    [Fact]
    public void Plan_MergeFieldsOnDeleteConflict_IsRejectedAsNotApplicable()
    {
        var plan = ReplicaConflictResolutionPlanner.Plan(
            Conflict(ReplicaConflictType.UpdateDelete),
            ReplicaConflictResolutionAction.MergeFields,
            Merge(("name", "\"merged\"")));

        plan.Rejection.Should().Be(ReplicaConflictResolutionRejection.NotApplicable);
    }

    [Fact]
    public void Plan_ChooseGeometryWithoutSource_IsRejectedAsInvalidRequest()
    {
        var plan = ReplicaConflictResolutionPlanner.Plan(
            Conflict(ReplicaConflictType.Geometry), ReplicaConflictResolutionAction.ChooseGeometry, NoInputs);

        plan.Rejection.Should().Be(ReplicaConflictResolutionRejection.InvalidRequest);
    }

    [Fact]
    public void Plan_ChooseGeometryServer_TakesServerGeometryAndCommittedAttributes()
    {
        var plan = ReplicaConflictResolutionPlanner.Plan(
            Conflict(ReplicaConflictType.Geometry, clientEditApplied: true),
            ReplicaConflictResolutionAction.ChooseGeometry,
            new ReplicaConflictResolutionInputs(FieldValues: null, GeometrySource: "server"));

        plan.IsAccepted.Should().BeTrue();
        using var document = JsonDocument.Parse(plan.FeatureStateJson!);
        document.RootElement.GetProperty("geometry").GetProperty("x").GetDouble().Should().Be(9.0);
        document.RootElement.GetProperty("attributes").GetProperty("name").GetString().Should().Be("client");
    }

    [Fact]
    public void Plan_ChooseGeometryWithoutCapturedGeometry_IsRejectedAsNotApplicable()
    {
        var plan = ReplicaConflictResolutionPlanner.Plan(
            Conflict(ReplicaConflictType.Geometry, serverState: """{"attributes":{"objectid":1}}"""),
            ReplicaConflictResolutionAction.ChooseGeometry,
            new ReplicaConflictResolutionInputs(FieldValues: null, GeometrySource: "server"));

        plan.Rejection.Should().Be(ReplicaConflictResolutionRejection.NotApplicable);
    }

    [Theory]
    [InlineData(ReplicaConflictType.Attachment)]
    [InlineData(ReplicaConflictType.Relationship)]
    public void Plan_UndetectableConflictClasses_AreRejectedRatherThanSilentlyAccepted(ReplicaConflictType type)
    {
        var plan = ReplicaConflictResolutionPlanner.Plan(
            Conflict(type), ReplicaConflictResolutionAction.AcceptClient, NoInputs);

        plan.Rejection.Should().Be(ReplicaConflictResolutionRejection.NotApplicable);
    }

    [Fact]
    public void Plan_AcceptClientWithoutCapturedClientState_IsRejectedAsNotApplicable()
    {
        var plan = ReplicaConflictResolutionPlanner.Plan(
            Conflict(clientState: null), ReplicaConflictResolutionAction.AcceptClient, NoInputs);

        plan.Rejection.Should().Be(ReplicaConflictResolutionRejection.NotApplicable);
    }

    [Fact]
    public void Plan_KeepServerWithoutCapturedServerState_IsRejectedAsNotApplicable()
    {
        var plan = ReplicaConflictResolutionPlanner.Plan(
            Conflict(clientEditApplied: true, serverState: null),
            ReplicaConflictResolutionAction.KeepServer,
            NoInputs);

        plan.Rejection.Should().Be(ReplicaConflictResolutionRejection.NotApplicable);
    }
}

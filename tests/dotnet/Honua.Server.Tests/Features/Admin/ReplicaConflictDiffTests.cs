// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Admin;
using Xunit;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Unit tests for the durable disconnected-sync conflict field/geometry diff (#1287). The diff powers
/// the Console conflict reviewer's per-field and geometry comparison; it must surface only diverging
/// fields and stay defensive about absent or malformed state envelopes.
/// </summary>
public sealed class ReplicaConflictDiffTests
{
    private static JsonElement State(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void Compute_WithBaseClientServer_FlagsDivergingSideOnly()
    {
        var baseState = State("""{"attributes":{"OBJECTID":100,"name":"base","status":"open"}}""");
        var clientState = State("""{"attributes":{"OBJECTID":100,"name":"client","status":"open"}}""");
        var serverState = State("""{"attributes":{"OBJECTID":100,"name":"base","status":"closed"}}""");

        var (geometryChanged, fieldChanges) = ReplicaConflictDiff.Compute(baseState, clientState, serverState);

        geometryChanged.Should().BeNull("no geometry token was captured in the states");

        // OBJECTID agrees everywhere and must be omitted; only name and status diverge.
        fieldChanges.Select(f => f.Field).Should().BeEquivalentTo(["name", "status"]);

        var name = fieldChanges.Single(f => f.Field == "name");
        name.ChangedOnClient.Should().BeTrue("client changed name from the base");
        name.ChangedOnServer.Should().BeFalse("server kept the base name");

        var status = fieldChanges.Single(f => f.Field == "status");
        status.ChangedOnClient.Should().BeFalse("client kept the base status");
        status.ChangedOnServer.Should().BeTrue("server changed status from the base");
    }

    [Fact]
    public void Compute_WithoutBase_FlagsBothSidesWhenClientAndServerDisagree()
    {
        var clientState = State("""{"attributes":{"name":"client"}}""");
        var serverState = State("""{"attributes":{"name":"server"}}""");

        var (_, fieldChanges) = ReplicaConflictDiff.Compute(baseState: null, clientState, serverState);

        var name = fieldChanges.Single();
        name.Field.Should().Be("name");
        name.ChangedOnClient.Should().BeTrue();
        name.ChangedOnServer.Should().BeTrue();
    }

    [Fact]
    public void Compute_WhenClientAndServerAgree_EmitsNoFieldChanges()
    {
        var clientState = State("""{"attributes":{"name":"same","count":5}}""");
        var serverState = State("""{"attributes":{"name":"same","count":5}}""");

        var (_, fieldChanges) = ReplicaConflictDiff.Compute(baseState: null, clientState, serverState);

        fieldChanges.Should().BeEmpty();
    }

    [Fact]
    public void Compute_WhenAStateIsMissing_ReturnsEmptyDiff()
    {
        var clientState = State("""{"attributes":{"name":"client"}}""");

        var (geometryChanged, fieldChanges) = ReplicaConflictDiff.Compute(baseState: null, clientState, serverState: null);

        geometryChanged.Should().BeNull();
        fieldChanges.Should().BeEmpty();
    }

    [Fact]
    public void Compute_WithDifferingGeometry_ReportsGeometryChanged()
    {
        var clientState = State("""{"attributes":{"id":1},"geometry":{"x":1,"y":2}}""");
        var serverState = State("""{"attributes":{"id":1},"geometry":{"x":9,"y":9}}""");

        var (geometryChanged, _) = ReplicaConflictDiff.Compute(baseState: null, clientState, serverState);

        geometryChanged.Should().BeTrue();
    }

    [Fact]
    public void Compute_WithIdenticalGeometry_ReportsGeometryUnchanged()
    {
        var clientState = State("""{"attributes":{"id":1},"geometry":{"x":1,"y":2}}""");
        var serverState = State("""{"attributes":{"id":1},"geometry":{"x":1,"y":2}}""");

        var (geometryChanged, _) = ReplicaConflictDiff.Compute(baseState: null, clientState, serverState);

        geometryChanged.Should().BeFalse();
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;

namespace Honua.Core.Tests.Features.FeatureStore;

/// <summary>
/// Unit tests for <see cref="ReplicaConflictClassifier"/>: the attach-time refinement of the coarse,
/// detection-time disconnected-sync conflict classification (#1287). The classifier only promotes the
/// generic <see cref="ReplicaConflictType.Attribute"/> bucket to
/// <see cref="ReplicaConflictType.Geometry"/> when the captured states show the attributes agree but
/// the geometry differs.
/// </summary>
public sealed class ReplicaConflictClassifierTests
{
    [Fact]
    public void Refine_AttributesAgreeAndGeometryDiffers_ReturnsGeometry()
    {
        var client = """{"attributes":{"OBJECTID":1,"name":"same"},"geometry":{"x":1.0,"y":2.0}}""";
        var server = """{"attributes":{"OBJECTID":1,"name":"same"},"geometry":{"x":9.0,"y":8.0}}""";

        ReplicaConflictClassifier.Refine(ReplicaConflictType.Attribute, client, server)
            .Should().Be(ReplicaConflictType.Geometry);
    }

    [Fact]
    public void Refine_AttributesAgreeIgnoringFieldOrderAndGeometryDiffers_ReturnsGeometry()
    {
        // Field ordering must not be treated as an attribute divergence.
        var client = """{"attributes":{"OBJECTID":1,"name":"same"},"geometry":{"x":1.0,"y":2.0}}""";
        var server = """{"attributes":{"name":"same","OBJECTID":1},"geometry":{"x":1.0,"y":3.0}}""";

        ReplicaConflictClassifier.Refine(ReplicaConflictType.Attribute, client, server)
            .Should().Be(ReplicaConflictType.Geometry);
    }

    [Fact]
    public void Refine_AttributesDiffer_KeepsAttribute()
    {
        var client = """{"attributes":{"OBJECTID":1,"name":"client"},"geometry":{"x":1.0,"y":2.0}}""";
        var server = """{"attributes":{"OBJECTID":1,"name":"server"},"geometry":{"x":9.0,"y":8.0}}""";

        ReplicaConflictClassifier.Refine(ReplicaConflictType.Attribute, client, server)
            .Should().Be(ReplicaConflictType.Attribute);
    }

    [Fact]
    public void Refine_AttributesAgreeButGeometryEqual_KeepsAttribute()
    {
        var client = """{"attributes":{"OBJECTID":1,"name":"same"},"geometry":{"x":1.0,"y":2.0}}""";
        var server = """{"attributes":{"OBJECTID":1,"name":"same"},"geometry":{"x":1.0,"y":2.0}}""";

        ReplicaConflictClassifier.Refine(ReplicaConflictType.Attribute, client, server)
            .Should().Be(ReplicaConflictType.Attribute);
    }

    [Fact]
    public void Refine_GeometryMissingOnOneSide_KeepsAttribute()
    {
        var client = """{"attributes":{"OBJECTID":1,"name":"same"},"geometry":{"x":1.0,"y":2.0}}""";
        var server = """{"attributes":{"OBJECTID":1,"name":"same"}}""";

        ReplicaConflictClassifier.Refine(ReplicaConflictType.Attribute, client, server)
            .Should().Be(ReplicaConflictType.Attribute);
    }

    [Theory]
    [InlineData(ReplicaConflictType.DeleteUpdate)]
    [InlineData(ReplicaConflictType.UpdateDelete)]
    [InlineData(ReplicaConflictType.DuplicateInsert)]
    public void Refine_NonAttributeConflict_IsNeverReclassified(ReplicaConflictType current)
    {
        // A geometry-only divergence must not promote an already-specific structural conflict type.
        var client = """{"attributes":{"OBJECTID":1,"name":"same"},"geometry":{"x":1.0,"y":2.0}}""";
        var server = """{"attributes":{"OBJECTID":1,"name":"same"},"geometry":{"x":9.0,"y":8.0}}""";

        ReplicaConflictClassifier.Refine(current, client, server).Should().Be(current);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("""{"attributes":{"name":"a"},"geometry":{"x":1,"y":2}}""", null)]
    [InlineData(null, """{"attributes":{"name":"a"},"geometry":{"x":1,"y":2}}""")]
    public void Refine_MissingState_KeepsAttribute(string? client, string? server)
    {
        ReplicaConflictClassifier.Refine(ReplicaConflictType.Attribute, client, server)
            .Should().Be(ReplicaConflictType.Attribute);
    }

    [Fact]
    public void Refine_MalformedJson_KeepsAttribute()
    {
        ReplicaConflictClassifier.Refine(ReplicaConflictType.Attribute, "{not json", "{also not json")
            .Should().Be(ReplicaConflictType.Attribute);
    }

    [Fact]
    public void Refine_ServerHasUntouchedExtraFields_StillReturnsGeometry()
    {
        // The client supplied only objectid + name (both matching the server); the server's extra
        // business field is not a client-side change and must not block geometry classification.
        var client = """{"attributes":{"OBJECTID":1,"name":"same"},"geometry":{"x":1.0,"y":2.0}}""";
        var server = """{"attributes":{"OBJECTID":1,"name":"same","category":"server-only"},"geometry":{"x":9.0,"y":8.0}}""";

        ReplicaConflictClassifier.Refine(ReplicaConflictType.Attribute, client, server)
            .Should().Be(ReplicaConflictType.Geometry);
    }

    [Fact]
    public void Refine_ClientSuppliedOnlyObjectIdAndGeometry_ReturnsGeometry()
    {
        var client = """{"attributes":{"OBJECTID":1},"geometry":{"x":1.0,"y":2.0}}""";
        var server = """{"attributes":{"OBJECTID":1,"name":"server"},"geometry":{"x":9.0,"y":8.0}}""";

        ReplicaConflictClassifier.Refine(ReplicaConflictType.Attribute, client, server)
            .Should().Be(ReplicaConflictType.Geometry);
    }

    [Fact]
    public void Refine_FieldNameCasingDiffers_TreatedAsSameField()
    {
        // objectid vs OBJECTID is the same field; only geometry changed → geometry conflict.
        var client = """{"attributes":{"objectid":1,"NAME":"same"},"geometry":{"x":1.0,"y":2.0}}""";
        var server = """{"attributes":{"OBJECTID":1,"name":"same"},"geometry":{"x":9.0,"y":8.0}}""";

        ReplicaConflictClassifier.Refine(ReplicaConflictType.Attribute, client, server)
            .Should().Be(ReplicaConflictType.Geometry);
    }
}

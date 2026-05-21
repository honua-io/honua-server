// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Spec.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Spec.Domain;

/// <summary>
/// Unit coverage for <see cref="ReferenceNode"/> canonical rendering and the
/// <see cref="LiteralNode"/> record. <c>ReferenceNode.Canonical</c> shows up
/// in diagnostic messages and canonical JSON output, so spelling rules must
/// stay pinned (#1144).
/// </summary>
public sealed class SpecNodeTests
{
    private static SourceSpan Span() => new(1, 1, 0, 0);

    [UnitTest]
    public void Canonical_RootOnly_PrependsAtSign()
    {
        var node = new ReferenceNode(Span(), "hospitals", ImmutableArray<string>.Empty);

        node.Canonical.Should().Be("@hospitals");
    }

    [UnitTest]
    public void Canonical_RootWithCall_AddsCallSuffix()
    {
        var node = new ReferenceNode(Span(), "hospitals", ImmutableArray<string>.Empty, Call: "count");

        node.Canonical.Should().Be("@hospitals.count()");
    }

    [UnitTest]
    public void Canonical_RootWithSegments_JoinsWithDots()
    {
        var node = new ReferenceNode(Span(), "compute", ImmutableArray.Create("near_rivers"));

        node.Canonical.Should().Be("@compute.near_rivers");
    }

    [UnitTest]
    public void Canonical_DottedChainWithCall_RendersFullPath()
    {
        var node = new ReferenceNode(Span(), "compute", ImmutableArray.Create("near_rivers"), Call: "count");

        node.Canonical.Should().Be("@compute.near_rivers.count()");
    }

    [UnitTest]
    public void Canonical_MultipleSegments_AreJoinedInOrder()
    {
        var node = new ReferenceNode(
            Span(),
            "map",
            ImmutableArray.Create("layers", "first", "style"));

        node.Canonical.Should().Be("@map.layers.first.style");
    }

    [UnitTest]
    public void LiteralNode_StringKind_RoundTripsValue()
    {
        var literal = new LiteralNode(Span(), SpecTypeKind.String, String: "hello");

        literal.Kind.Should().Be(SpecTypeKind.String);
        literal.String.Should().Be("hello");
        literal.Number.Should().BeNull();
        literal.Integer.Should().BeNull();
        literal.Boolean.Should().BeNull();
        literal.Unit.Should().BeNull();
    }

    [UnitTest]
    public void LiteralNode_DistanceKind_CarriesUnitAndNumericValue()
    {
        var literal = new LiteralNode(Span(), SpecTypeKind.Distance, Number: 5.0, Unit: "km");

        literal.Kind.Should().Be(SpecTypeKind.Distance);
        literal.Number.Should().Be(5.0);
        literal.Unit.Should().Be("km");
    }

    [UnitTest]
    public void LiteralNode_IntegerKind_RoundTripsValue()
    {
        var literal = new LiteralNode(Span(), SpecTypeKind.Integer, Integer: 42);

        literal.Integer.Should().Be(42);
    }

    [UnitTest]
    public void LiteralNode_BooleanKind_RoundTripsValue()
    {
        var literal = new LiteralNode(Span(), SpecTypeKind.Boolean, Boolean: true);

        literal.Boolean.Should().BeTrue();
    }

    [UnitTest]
    public void LiteralNode_InferredType_StartsNull()
    {
        var literal = new LiteralNode(Span(), SpecTypeKind.String, String: "x");

        literal.InferredType.Should().BeNull();
    }

    [UnitTest]
    public void LiteralNode_InferredType_CanBeSetViaRecordWith()
    {
        var literal = new LiteralNode(Span(), SpecTypeKind.String, String: "x");

        var withType = literal with { InferredType = TypeRef.Intrinsic(SpecTypeKind.String) };

        withType.InferredType.Should().Be(TypeRef.Intrinsic(SpecTypeKind.String));
        literal.InferredType.Should().BeNull(); // original untouched
    }

    [UnitTest]
    public void ReferenceNode_SameSegmentArray_AreEqual()
    {
        // ImmutableArray uses reference equality in the record's default
        // Equals implementation — sharing the segment array exercises the
        // value equality of the surrounding record fields.
        var segments = ImmutableArray.Create("x");
        var a = new ReferenceNode(Span(), "h", segments);
        var b = new ReferenceNode(Span(), "h", segments);

        a.Should().Be(b);
    }
}

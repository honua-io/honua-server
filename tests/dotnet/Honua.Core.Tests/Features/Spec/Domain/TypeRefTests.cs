// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Spec.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Spec.Domain;

/// <summary>
/// Unit coverage for <see cref="TypeRef"/> — the type checker's assignability
/// rules and the intrinsic-cache fast path. These rules are user-observable
/// through spec diagnostic messages, so they need to be pinned down (#1144).
/// </summary>
public sealed class TypeRefTests
{
    [UnitTest]
    public void Intrinsic_ReturnsSharedInstance_ForKnownKinds()
    {
        var a = TypeRef.Intrinsic(SpecTypeKind.String);
        var b = TypeRef.Intrinsic(SpecTypeKind.String);

        a.Should().BeSameAs(b);
        a.Kind.Should().Be(SpecTypeKind.String);
        a.ElementTypes.Should().BeNull();
        a.Crs.Should().BeNull();
    }

    [UnitTest]
    public void Intrinsic_CoversEveryEnumKind()
    {
        // Every enum value must round-trip through Intrinsic so that the type
        // checker never produces a null TypeRef during literal classification.
        foreach (var kind in Enum.GetValues<SpecTypeKind>())
        {
            var typeRef = TypeRef.Intrinsic(kind);
            typeRef.Should().NotBeNull();
            typeRef.Kind.Should().Be(kind);
        }
    }

    [UnitTest]
    public void IsAssignableTo_UnknownTarget_AlwaysTrue()
    {
        // Unknown acts as a "wildcard"; the type checker uses it as a recovery
        // sentinel when an upstream node failed inference.
        TypeRef.Intrinsic(SpecTypeKind.Dataset)
            .IsAssignableTo(TypeRef.Intrinsic(SpecTypeKind.Unknown)).Should().BeTrue();
        TypeRef.Intrinsic(SpecTypeKind.Geometry)
            .IsAssignableTo(TypeRef.Intrinsic(SpecTypeKind.Unknown)).Should().BeTrue();
    }

    [UnitTest]
    public void IsAssignableTo_NullTarget_ReturnsFalse()
    {
        TypeRef.Intrinsic(SpecTypeKind.String).IsAssignableTo(null!).Should().BeFalse();
    }

    [UnitTest]
    public void IsAssignableTo_StringSatisfiesCrsPort()
    {
        // CRS identifiers are passed as plain strings (e.g. "EPSG:3857") so a
        // String literal must satisfy a Crs-typed parameter port.
        TypeRef.Intrinsic(SpecTypeKind.String)
            .IsAssignableTo(TypeRef.Intrinsic(SpecTypeKind.Crs))
            .Should().BeTrue();
    }

    [UnitTest]
    public void IsAssignableTo_DifferentKinds_AreIncompatible()
    {
        TypeRef.Intrinsic(SpecTypeKind.Number)
            .IsAssignableTo(TypeRef.Intrinsic(SpecTypeKind.String))
            .Should().BeFalse();

        TypeRef.Intrinsic(SpecTypeKind.Distance)
            .IsAssignableTo(TypeRef.Intrinsic(SpecTypeKind.Duration))
            .Should().BeFalse();
    }

    [UnitTest]
    public void IsAssignableTo_SameKindWithoutElementTypes_AreAssignable()
    {
        TypeRef.Intrinsic(SpecTypeKind.Geometry)
            .IsAssignableTo(TypeRef.Intrinsic(SpecTypeKind.Geometry))
            .Should().BeTrue();
    }

    [UnitTest]
    public void IsAssignableTo_DatasetAcceptsAnyElements_WhenTargetUnspecified()
    {
        var source = new TypeRef(SpecTypeKind.Dataset, ElementTypes: ["id", "name"]);
        var target = TypeRef.Intrinsic(SpecTypeKind.Dataset);

        source.IsAssignableTo(target).Should().BeTrue();
    }

    [UnitTest]
    public void IsAssignableTo_DatasetRequiresSupersetSource_WhenTargetHasElements()
    {
        // Target requires {id, name}; source has those plus extras → assignable.
        var source = new TypeRef(SpecTypeKind.Dataset, ElementTypes: ["id", "name", "extra"]);
        var target = new TypeRef(SpecTypeKind.Dataset, ElementTypes: ["id", "name"]);

        source.IsAssignableTo(target).Should().BeTrue();
    }

    [UnitTest]
    public void IsAssignableTo_DatasetWithoutSourceElements_RejectsTypedTarget()
    {
        var source = TypeRef.Intrinsic(SpecTypeKind.Dataset);
        var target = new TypeRef(SpecTypeKind.Dataset, ElementTypes: ["id"]);

        source.IsAssignableTo(target).Should().BeFalse();
    }

    [UnitTest]
    public void IsAssignableTo_DatasetMissingRequiredElement_ReturnsFalse()
    {
        var source = new TypeRef(SpecTypeKind.Dataset, ElementTypes: ["id"]);
        var target = new TypeRef(SpecTypeKind.Dataset, ElementTypes: ["id", "name"]);

        source.IsAssignableTo(target).Should().BeFalse();
    }

    [UnitTest]
    public void IsAssignableTo_DatasetElementMatching_IsCaseInsensitive()
    {
        var source = new TypeRef(SpecTypeKind.Dataset, ElementTypes: ["Id", "NAME"]);
        var target = new TypeRef(SpecTypeKind.Dataset, ElementTypes: ["id", "name"]);

        source.IsAssignableTo(target).Should().BeTrue();
    }

    [UnitTest]
    public void IsAssignableTo_RasterFollowsSameElementRules()
    {
        var source = new TypeRef(SpecTypeKind.Raster, ElementTypes: ["band1", "band2"]);
        var target = new TypeRef(SpecTypeKind.Raster, ElementTypes: ["band1"]);

        source.IsAssignableTo(target).Should().BeTrue();
    }

    [UnitTest]
    public void IsAssignableTo_EmptyTargetElementList_AcceptsAnySource()
    {
        var source = new TypeRef(SpecTypeKind.Dataset, ElementTypes: ["a"]);
        var target = new TypeRef(SpecTypeKind.Dataset, ElementTypes: []);

        source.IsAssignableTo(target).Should().BeTrue();
    }

    [UnitTest]
    public void ToString_PrimitiveKind_RendersLowercase()
    {
        TypeRef.Intrinsic(SpecTypeKind.Number).ToString().Should().Be("number");
        TypeRef.Intrinsic(SpecTypeKind.Geometry).ToString().Should().Be("geometry");
    }

    [UnitTest]
    public void ToString_DatasetWithElements_RendersAngleBrackets()
    {
        var dataset = new TypeRef(SpecTypeKind.Dataset, ElementTypes: ["id", "name"]);

        dataset.ToString().Should().Be("dataset<id,name>");
    }

    [UnitTest]
    public void ToString_RasterWithElements_RendersAngleBrackets()
    {
        var raster = new TypeRef(SpecTypeKind.Raster, ElementTypes: ["band1"]);

        raster.ToString().Should().Be("raster<band1>");
    }

    [UnitTest]
    public void ToString_DatasetWithoutElements_OmitsAngleBrackets()
    {
        TypeRef.Intrinsic(SpecTypeKind.Dataset).ToString().Should().Be("dataset");
    }

    [UnitTest]
    public void Records_EqualByValue()
    {
        var a = new TypeRef(SpecTypeKind.Distance, ElementTypes: null, Crs: "EPSG:3857");
        var b = new TypeRef(SpecTypeKind.Distance, ElementTypes: null, Crs: "EPSG:3857");

        a.Should().Be(b);
        (a == b).Should().BeTrue();
    }
}

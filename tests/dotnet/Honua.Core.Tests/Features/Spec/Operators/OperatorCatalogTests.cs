// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Spec.Domain;
using Honua.Core.Features.Spec.Operators;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Spec.Operators;

/// <summary>
/// Unit coverage for the in-code S1 <see cref="OperatorCatalog"/> and the
/// <see cref="OperatorSignature"/> lookup helpers. Anchoring the catalog shape
/// keeps the operator advertisement contract honest across releases (#1144).
/// </summary>
public sealed class OperatorCatalogTests
{
    private static readonly string[] s_expectedOperators =
    [
        "filter",
        "spatial_join",
        "buffer",
        "reproject",
        "zonal_stats",
        "slope",
    ];

    [UnitTest]
    public void Catalog_AdvertisesCurrentCapabilityVersion()
    {
        var catalog = new OperatorCatalog();

        catalog.CapabilityVersion.Should().Be(SpecGrammarVersion.CurrentOperatorCapability);
    }

    [UnitTest]
    public void Catalog_ExposesEveryS1Operator()
    {
        var catalog = new OperatorCatalog();

        catalog.OperatorNames.Should().BeEquivalentTo(s_expectedOperators);
    }

    [Theory]
    [InlineData("filter")]
    [InlineData("spatial_join")]
    [InlineData("buffer")]
    [InlineData("reproject")]
    [InlineData("zonal_stats")]
    [InlineData("slope")]
    public void Lookup_KnownOperator_ReturnsSignature(string name)
    {
        var catalog = new OperatorCatalog();

        var sig = catalog.Lookup(name);

        sig.Should().NotBeNull();
        sig!.Name.Should().Be(name);
    }

    [UnitTest]
    public void Lookup_UnknownOperator_ReturnsNull()
    {
        new OperatorCatalog().Lookup("does_not_exist").Should().BeNull();
    }

    [UnitTest]
    public void Lookup_IsOrdinalCaseSensitive()
    {
        // The spec language is case-sensitive — "FILTER" and "filter" are
        // intentionally different identifiers so the catalog must not match
        // case-insensitively.
        new OperatorCatalog().Lookup("FILTER").Should().BeNull();
    }

    [UnitTest]
    public void FilterSignature_DeclaresRequiredWhereParameter()
    {
        var sig = new OperatorCatalog().Lookup("filter")!;

        sig.Inputs.Should().ContainSingle(p => p.Name == "input");
        var whereParam = sig.FindParameter("where");
        whereParam.Should().NotBeNull();
        whereParam!.Required.Should().BeTrue();
        whereParam.Type.Kind.Should().Be(SpecTypeKind.String);
        sig.Output.Kind.Should().Be(SpecTypeKind.Dataset);
    }

    [UnitTest]
    public void BufferSignature_RequiresDistance_AndIsCrsSensitive()
    {
        var sig = new OperatorCatalog().Lookup("buffer")!;

        sig.CrsSensitive.Should().BeTrue();
        var distance = sig.FindParameter("distance");
        distance.Should().NotBeNull();
        distance!.Required.Should().BeTrue();
        distance.Type.Kind.Should().Be(SpecTypeKind.Distance);
        sig.FindParameter("crs")!.Required.Should().BeFalse();
    }

    [UnitTest]
    public void SpatialJoinSignature_HasTwoDatasetInputs_AndOptionalCrsParam()
    {
        var sig = new OperatorCatalog().Lookup("spatial_join")!;

        sig.Inputs.Select(i => i.Name).Should().Equal("left", "right");
        sig.Inputs.All(i => i.Type.Kind == SpecTypeKind.Dataset).Should().BeTrue();
        sig.CrsSensitive.Should().BeTrue();
        sig.FindParameter("distance")!.Required.Should().BeFalse();
        sig.FindParameter("crs")!.Required.Should().BeFalse();
    }

    [UnitTest]
    public void ReprojectSignature_HasUnknownOutput_ForElementForwarding()
    {
        // reproject preserves the input's kind (dataset → dataset, raster →
        // raster). The signature signals this by declaring Output=Unknown so
        // the type checker forwards the input kind explicitly.
        var sig = new OperatorCatalog().Lookup("reproject")!;

        sig.Output.Kind.Should().Be(SpecTypeKind.Unknown);
        sig.FindParameter("crs")!.Required.Should().BeTrue();
    }

    [UnitTest]
    public void ZonalStatsSignature_TakesDatasetZonesAndRasterValues()
    {
        var sig = new OperatorCatalog().Lookup("zonal_stats")!;

        sig.FindInput("zones")!.Type.Kind.Should().Be(SpecTypeKind.Dataset);
        sig.FindInput("values")!.Type.Kind.Should().Be(SpecTypeKind.Raster);
        sig.Output.Kind.Should().Be(SpecTypeKind.Dataset);
        sig.FindParameter("statistics")!.Required.Should().BeFalse();
    }

    [UnitTest]
    public void SlopeSignature_OperatesOnRasterAndReturnsRaster()
    {
        var sig = new OperatorCatalog().Lookup("slope")!;

        sig.Inputs.Should().ContainSingle();
        sig.FindInput("input")!.Type.Kind.Should().Be(SpecTypeKind.Raster);
        sig.Output.Kind.Should().Be(SpecTypeKind.Raster);
    }

    [UnitTest]
    public void FindParameter_UnknownName_ReturnsNull()
    {
        new OperatorCatalog().Lookup("filter")!
            .FindParameter("nope").Should().BeNull();
    }

    [UnitTest]
    public void FindInput_UnknownName_ReturnsNull()
    {
        new OperatorCatalog().Lookup("filter")!
            .FindInput("missing").Should().BeNull();
    }

    [UnitTest]
    public void Signature_DefaultCrsSensitivity_IsFalse()
    {
        var sig = new OperatorSignature(
            Name: "custom",
            Inputs: ImmutableArray<OperatorPort>.Empty,
            Parameters: ImmutableArray<OperatorPort>.Empty,
            Output: TypeRef.Intrinsic(SpecTypeKind.Dataset));

        sig.CrsSensitive.Should().BeFalse();
    }

    [UnitTest]
    public void OperatorPort_DefaultsToRequired()
    {
        var port = new OperatorPort("p", TypeRef.Intrinsic(SpecTypeKind.String));

        port.Required.Should().BeTrue();
    }
}

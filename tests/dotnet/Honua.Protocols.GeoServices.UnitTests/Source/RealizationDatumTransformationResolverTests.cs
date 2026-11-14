// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Protocols.GeoServices;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices;

public sealed class RealizationDatumTransformationResolverTests
{
    private readonly IDatumTransformationCatalog _catalog = GeoServicesDatumTransformationCatalog.Create();

    [Theory]
    [InlineData(null)]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("2020,5")]
    public void Resolve_TimedOperationWithoutFiniteEpoch_RejectsRequest(string? epoch)
    {
        var result = GeoServicesDatumTransformationResolver.Resolve(_catalog,
            new GeoServicesDatumTransformationResolver.ResolutionRequest
            {
                Value = "108574", FromSrid = 7912, ToSrid = 6319, CoordinateEpoch = epoch
            });

        result.Selection.Should().BeNull();
        result.Error.Should().Contain("coordinateEpoch");
    }

    [Theory]
    [InlineData(7912, 6319)]
    [InlineData(6319, 7912)]
    public void Resolve_TimedOperation_BindsEpochAndPreservesDirection(int from, int to)
    {
        var result = GeoServicesDatumTransformationResolver.Resolve(_catalog,
            new GeoServicesDatumTransformationResolver.ResolutionRequest
            {
                Value = "108574", FromSrid = from, ToSrid = to, CoordinateEpoch = "2020.25"
            });

        result.Error.Should().BeNull();
        var selection = result.Selection;
        selection!.CoordinateEpoch.Should().Be(2020.25);
        selection.TransformForward.Should().Be(from == 7912);
        selection.ProjPipeline.Should().Contain("+v_4=2020.25").And.NotContain("{coordinateEpoch}");
    }

    [Theory]
    [InlineData(4759, 4326)]
    [InlineData(4326, 4759)]
    public void Resolve_NamedComposite_PreservesDirection(int from, int to)
    {
        GeoServicesDatumTransformationResolver.TryResolve(
            _catalog, "Honua:WGS84_ITRF00_To_NAD83_NSRS2007_Via_2011", from, to, out var selection, out var error).Should().BeTrue(error);

        selection!.Wkid.Should().BeNull();
        selection.TransformForward.Should().Be(from == 4326);
        selection.ProjPipeline.Should().Contain("+proj=helmert");
    }

    [UnitTest]
    public void Resolve_TimedDefaultWithoutEpoch_RejectsRequest()
    {
        GeoServicesDatumTransformationResolver.TryResolve(
            _catalog, null, 7912, 6319, out _, out var error).Should().BeFalse();

        error.Should().Contain("coordinateEpoch");
    }

    [Theory]
    [InlineData(4269)]
    [InlineData(4152)]
    [InlineData(4759)]
    [InlineData(6318)]
    public void Resolve_OmittedOperationAndEpoch_PreservesIdentityDefault(int source)
    {
        GeoServicesDatumTransformationResolver.TryResolve(
            _catalog, null, source, 4326, out var selection, out var error).Should().BeTrue(error);

        selection!.ProjPipeline.Should().Be("+proj=noop");
        selection.CoordinateEpoch.Should().BeNull();
    }
}

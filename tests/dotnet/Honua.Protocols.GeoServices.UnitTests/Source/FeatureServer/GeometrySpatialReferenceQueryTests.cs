// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class GeometrySpatialReferenceQueryTests
{
    [Fact]
    public void DecideMissingInputSrid_UnresolvableWkidWithoutInSr_RejectsGeometrySpatialReference()
    {
        var decision = FeatureServerQueryHandler.DecideMissingInputSrid(
            inSrSpecified: false,
            resolvedSrid: null,
            geometrySpatialReference: new GeoServicesSpatialReference { Wkid = 999999 });

        decision.Should().Be(FeatureServerQueryHandler.MissingInputSridDecision.RejectGeometrySpatialReference);
    }

    [Fact]
    public void DecideMissingInputSrid_UnresolvableWktWithoutInSr_RejectsGeometrySpatialReference()
    {
        var decision = FeatureServerQueryHandler.DecideMissingInputSrid(
            inSrSpecified: false,
            resolvedSrid: null,
            geometrySpatialReference: new GeoServicesSpatialReference
            {
                Wkt = "PROJCS[\"custom_local\",GEOGCS[\"GCS_WGS_1984\",DATUM[\"D_WGS_1984\",SPHEROID[\"WGS_1984\",6378137,298.257223563]],PRIMEM[\"Greenwich\",0],UNIT[\"Degree\",0.0174532925199433]]]"
            });

        decision.Should().Be(FeatureServerQueryHandler.MissingInputSridDecision.RejectGeometrySpatialReference);
    }

    [Fact]
    public void DecideMissingInputSrid_LatestWkidOnly_RejectsGeometrySpatialReference()
    {
        var decision = FeatureServerQueryHandler.DecideMissingInputSrid(
            inSrSpecified: false,
            resolvedSrid: null,
            geometrySpatialReference: new GeoServicesSpatialReference { LatestWkid = 999999 });

        decision.Should().Be(FeatureServerQueryHandler.MissingInputSridDecision.RejectGeometrySpatialReference);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void DecideMissingInputSrid_GeometryWithoutSpatialReference_FallsBackToLayer(int? wkid)
    {
        GeoServicesSpatialReference? spatialReference = wkid is null
            ? null
            : new GeoServicesSpatialReference { Wkid = wkid };

        var decision = FeatureServerQueryHandler.DecideMissingInputSrid(
            inSrSpecified: false,
            resolvedSrid: null,
            geometrySpatialReference: spatialReference);

        decision.Should().Be(FeatureServerQueryHandler.MissingInputSridDecision.FallBackToLayer);
    }

    [Fact]
    public void DecideMissingInputSrid_EmptySpatialReferenceObject_FallsBackToLayer()
    {
        var decision = FeatureServerQueryHandler.DecideMissingInputSrid(
            inSrSpecified: false,
            resolvedSrid: null,
            geometrySpatialReference: new GeoServicesSpatialReference());

        decision.Should().Be(FeatureServerQueryHandler.MissingInputSridDecision.FallBackToLayer);
    }

    [Fact]
    public void DecideMissingInputSrid_InvalidInSr_RejectsInSrBeforeGeometryFallback()
    {
        var decision = FeatureServerQueryHandler.DecideMissingInputSrid(
            inSrSpecified: true,
            resolvedSrid: null,
            geometrySpatialReference: null);

        decision.Should().Be(FeatureServerQueryHandler.MissingInputSridDecision.RejectInSr);
    }

    [Fact]
    public void DecideMissingInputSrid_ResolvedSrid_UsesResolvedValue()
    {
        var decision = FeatureServerQueryHandler.DecideMissingInputSrid(
            inSrSpecified: false,
            resolvedSrid: 4326,
            geometrySpatialReference: new GeoServicesSpatialReference { Wkid = 4326 });

        decision.Should().Be(FeatureServerQueryHandler.MissingInputSridDecision.UseResolved);
    }
}

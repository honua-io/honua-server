// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Infrastructure.Services;
using Honua.Protocols.GeoServices.GeometryService.Services;
using Honua.TestKit.Attributes;
using NetTopologySuite.Densify;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GeometryService;

// What one GeometryServer request may generate. In process; the component host is not needed.
[Protocol(TestProtocols.GeometryService)]
public sealed class GeometryServiceVertexBudgetTests
{
    private const long Megabyte = 1024 * 1024;
    private const GeometryServiceCaller SignedIn = GeometryServiceCaller.SignedIn;
    private const GeometryServiceCaller NotSignedIn = GeometryServiceCaller.NotSignedIn;

    [UnitTest]
    [Operation(Operations.Densify)]
    public void CallerNotSignedIn_MayGenerateOneGeometryOfVertices()
    {
        var budget = new GeometryServiceVertexBudget(50_000, NotSignedIn);

        budget.PerGeometry.Should().Be(50_000);
        budget.PerRequest.Should().Be(50_000);
        budget.TryReserve(50_000).Should().BeTrue();
        budget.TryReserve(1).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Densify)]
    public void SignedInCaller_MayGenerateAFewGeometriesOfVertices()
    {
        var budget = new GeometryServiceVertexBudget(50_000, SignedIn);

        budget.PerRequest.Should().Be(50_000L * GeometryServiceVertexBudget.SignedInGeometriesPerRequest);
        for (var geometry = 0; geometry < GeometryServiceVertexBudget.SignedInGeometriesPerRequest; geometry++)
        {
            budget.TryReserve(50_000).Should().BeTrue();
        }

        budget.TryReserve(1).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Densify)]
    public void OneGeometryAboveItsLimit_IsRefusedForEveryCaller()
    {
        new GeometryServiceVertexBudget(50_000, NotSignedIn).TryReserve(50_001).Should().BeFalse();
        new GeometryServiceVertexBudget(50_000, SignedIn).TryReserve(50_001).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Densify)]
    public void RefusedGeometry_LeavesTheRequestTotalUnchanged()
    {
        var budget = new GeometryServiceVertexBudget(1_000, NotSignedIn);

        budget.TryReserve(600).Should().BeTrue();
        budget.TryReserve(600).Should().BeFalse();
        budget.TryReserve(400).Should().BeTrue();
        budget.TryReserve(0.5).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Length)]
    public void OneGeometryCheck_CountsNothingTowardsTheRequest()
    {
        var budget = new GeometryServiceVertexBudget(1_000, NotSignedIn);

        for (var geometry = 0; geometry < 10; geometry++)
        {
            budget.FitsOneGeometry(1_000).Should().BeTrue();
        }

        budget.FitsOneGeometry(1_001).Should().BeFalse();
        budget.FitsOneGeometry(double.NaN).Should().BeFalse();
        budget.TryReserve(1_000).Should().BeTrue();
    }

    [UnitTheory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1d)]
    [Operation(Operations.Densify)]
    public void EstimateThatIsNotACount_IsRefused(double estimate)
    {
        new GeometryServiceVertexBudget(50_000, SignedIn).TryReserve(estimate).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Densify)]
    public void EmptyGeometry_GeneratesNothingAndIsAccepted()
    {
        var budget = new GeometryServiceVertexBudget(50_000, NotSignedIn);

        budget.TryReserve(GeometryServiceHandler.EstimateDensifiedVertices(0, 0.001, 1)).Should().BeTrue();
        budget.TryReserve(50_000).Should().BeTrue();
    }

    [UnitTheory]
    [InlineData(10_000, 10_000)]
    [InlineData(100_000, 100_000)]
    [InlineData(5_000_000, GeometryServiceVertexBudget.PerGeometryCeiling)]
    [InlineData(0, 50_000)]
    [InlineData(-5, 50_000)]
    [Operation(Operations.Densify)]
    public void Budget_FollowsTheConfiguredGeometryVertexLimit(int configured, int expected)
    {
        var limits = new LimitsOptions();
        limits.Geometry.MaxVerticesPerGeometry = configured;

        var budget = GeometryServiceVertexBudget.Create(GeometryServiceTestContexts.Create(NotSignedIn, limits));

        budget.PerGeometry.Should().Be(expected);
        budget.PerRequest.Should().Be(expected);
    }

    [UnitTest]
    [Operation(Operations.Densify)]
    public void Budget_WithoutConfiguration_UsesTheDefaultGeometryVertexLimit()
    {
        var budget = GeometryServiceVertexBudget.Create(GeometryServiceTestContexts.CreateWithoutLimits());

        budget.PerGeometry.Should().Be(new GeometryLimits().MaxVerticesPerGeometry);
        budget.PerRequest.Should().Be(budget.PerGeometry);
    }

    [UnitTest]
    [Operation(Operations.Densify)]
    public void Budget_RecognisesASignedInCaller()
    {
        GeometryServiceVertexBudget.Create(GeometryServiceTestContexts.Create(SignedIn)).PerRequest
            .Should().Be(50_000L * GeometryServiceVertexBudget.SignedInGeometriesPerRequest);
        GeometryServiceVertexBudget.Create(GeometryServiceTestContexts.Create(NotSignedIn)).PerRequest
            .Should().Be(50_000);
    }

    [UnitTest]
    [Operation(Operations.Densify)]
    public void DensifyEstimate_FarAboveTheGeometryLimit_IsRefusedForEveryCaller()
    {
        var estimate = GeometryServiceHandler.EstimateDensifiedVertices(16_000_000, 1, 1);

        estimate.Should().Be(16_000_000);
        foreach (var caller in new[] { NotSignedIn, SignedIn })
        {
            var budget = GeometryServiceVertexBudget.Create(GeometryServiceTestContexts.Create(caller));

            budget.TryReserve(estimate).Should().BeFalse();
            budget.TryReserve(estimate / 100).Should().BeFalse();
        }
    }

    [UnitTest]
    [Operation(Operations.Densify)]
    public void DensifyEstimate_GeodesicLength_IsMeasuredInTheSegmentUnit()
    {
        // One degree of a geographic line is about 111 km of 1 km segments.
        GeometryServiceHandler.EstimateDensifiedVertices(1, 1_000, 111_320).Should().BeApproximately(111.32, 1e-9);
    }

    [UnitTest]
    [Operation(Operations.Densify)]
    public void LargestConfigurableBudget_DensifiedResult_AllocatesWithinBound()
    {
        // The largest request the limits allow: a signed-in caller with the geometry vertex
        // limit at the top of its range. Densifying and converting the result for the
        // response must stay within this bound on the request thread.
        const long allocationBound = 512 * Megabyte;
        var budget = new GeometryServiceVertexBudget(GeometryServiceVertexBudget.PerGeometryCeiling, SignedIn);
        var line = new GeometryFactory().CreateLineString([new Coordinate(0, 0), new Coordinate(budget.PerGeometry, 0)]);
        var converter = new GeometryConverter();
        var writer = new WKBWriter();
        // Type initialisation is not part of a request.
        converter.ConvertWkbToGeoServicesGeometry(writer.Write(Densifier.Densify(line, budget.PerGeometry / 4d)), 3857);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var generated = 0L;
        while (budget.TryReserve(GeometryServiceHandler.EstimateDensifiedVertices(line.Length, 1, 1)))
        {
            var densified = Densifier.Densify(line, 1);
            generated += densified.NumPoints - line.NumPoints;
            converter.ConvertWkbToGeoServicesGeometry(writer.Write(densified), 3857);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        generated.Should().Be(budget.PerRequest - GeometryServiceVertexBudget.SignedInGeometriesPerRequest);
        allocated.Should().BeLessThan(allocationBound, "measured {0} MiB", allocated / Megabyte);
    }
}

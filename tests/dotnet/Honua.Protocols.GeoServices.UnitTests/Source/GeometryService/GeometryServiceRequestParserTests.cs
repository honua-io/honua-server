// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices.GeometryService.Services;

namespace Honua.Protocols.GeoServices.UnitTests.Source.GeometryService;

public sealed class GeometryServiceRequestParserTests
{
    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("10,NaN")]
    [InlineData("10,Infinity")]
    [InlineData("10,-Infinity")]
    public void ParseDoubleArray_NonFiniteCommaSeparatedValues_ReturnsError(string raw)
    {
        var (values, error) = GeometryServiceRequestParser.ParseDoubleArray(raw);

        values.Should().BeEmpty();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void ParseDoubleArray_FiniteValues_Succeeds()
    {
        var (csvValues, csvError) = GeometryServiceRequestParser.ParseDoubleArray("10, 25.5, -5");
        csvError.Should().BeNull();
        csvValues.Should().Equal(10d, 25.5d, -5d);

        var (jsonValues, jsonError) = GeometryServiceRequestParser.ParseDoubleArray("[10, 25.5, -5]");
        jsonError.Should().BeNull();
        jsonValues.Should().Equal(10d, 25.5d, -5d);
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("""{"geometryType":"esriGeometryPoint","geometries":[{"x":0,"y":0,"spatialReference":{"wkid":"4326; DROP TABLE spatial_ref_sys--"}}]}""")]
    [InlineData("""{"geometryType":"esriGeometryPoint","geometries":[{"x":0,"y":0,"spatialReference":{"wkt":"'; DROP TABLE spatial_ref_sys--"}}]}""")]
    [InlineData("""{"geometryType":"esriGeometryPoint","geometries":[{"x":0,"y":0,"spatialReference":{"wkt":"GEOGCS[\"WGS 84\"]; DROP TABLE spatial_ref_sys--"}}]}""")]
    [InlineData("""{"geometryType":"esriGeometryPoint","geometries":[{"x":0,"y":0,"spatialReference":{"wkid":-1}}]}""")]
    [InlineData("""{"geometryType":"esriGeometryPoint","geometries":[{"x":0,"y":0,"spatialReference":"4326; DROP TABLE spatial_ref_sys--"}]}""")]
    public void ParseGeometries_HostileEmbeddedSpatialReferenceInjection_ReturnsError(string raw)
    {
        var (geometries, _, error) = GeometryServiceRequestParser.ParseGeometries(raw, 10, 10_000);

        geometries.Should().BeEmpty();
        error.Should().NotBeNullOrWhiteSpace();
        error.Should().NotContain("DROP TABLE");
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("""{"x":0,"y":0,"spatialReference":{"wkt":"'; DROP TABLE spatial_ref_sys--"}}""")]
    [InlineData("""{"x":0,"y":0,"spatialReference":{"wkid":"4326; DROP TABLE spatial_ref_sys--"}}""")]
    public void ParseSingleGeometry_HostileEmbeddedSpatialReferenceInjection_ReturnsError(string raw)
    {
        var (geometry, error) = GeometryServiceRequestParser.ParseSingleGeometry(raw);

        geometry.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
        error.Should().NotContain("DROP TABLE");
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.OData.Models;
using Honua.Protocols.OData.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.OData;

public sealed class ODataCrsUtilitiesTests
{
    [UnitTest]
    public async Task ResolveAxisOrderAsync_WithEpsg4326_ReturnsEastNorth()
    {
        var registry = new StubCrsRegistry(
            new CrsDefinition(
                "http://www.opengis.net/def/crs/EPSG/0/4326",
                4326,
                AxisOrder.NorthEast,
                true));

        var axisOrder = await ODataCrsUtilities.ResolveAxisOrderAsync(registry, 4326, default);

        axisOrder.Should().Be(AxisOrder.EastNorth);
    }

    [UnitTest]
    public async Task ResolveAxisOrderAsync_WithUnsupportedSrid_ReturnsEastNorthFallback()
    {
        var registry = new StubCrsRegistry(null);

        var axisOrder = await ODataCrsUtilities.ResolveAxisOrderAsync(registry, 999999, default);
        axisOrder.Should().Be(AxisOrder.EastNorth);
    }

    [UnitTest]
    public async Task TryResolveGeometryCrsAsync_WithEpsg4326_PreservesRegistryDefinition()
    {
        var registry = new StubCrsRegistry(
            new CrsDefinition(
                "http://www.opengis.net/def/crs/EPSG/0/4326",
                4326,
                AxisOrder.NorthEast,
                true));

        var geometry = new ODataSpatialGeometry
        {
            Type = "Point",
            CoordinatesJson = "[10,20]",
            Crs = new ODataSpatialCrs
            {
                Type = "name",
                Properties = new ODataSpatialCrsProperties
                {
                    Name = "EPSG:4326"
                }
            }
        };

        var result = await ODataCrsUtilities.TryResolveGeometryCrsAsync(registry, geometry, default);

        result.IsValid.Should().BeTrue(result.ErrorMessage);
        result.Definition.Should().NotBeNull();
        var definition = result.Definition!.Value;
        definition.Uri.Should().Be("http://www.opengis.net/def/crs/EPSG/0/4326");
        definition.Srid.Should().Be(4326);
        definition.AxisOrder.Should().Be(AxisOrder.EastNorth);
        definition.IsGeographic.Should().BeTrue();
    }

    [UnitTheory]
    [InlineData("urn:ogc:def:crs:EPSG:6.9:25831", 25831, false)]
    [InlineData("urn:x-ogc:def:crs:EPSG::4326", 4326, true)]
    [InlineData("urn:ogc:def:crs:OGC:1.3:CRS84", 4326, true)]
    [InlineData("urn:ogc:def:crs:OGC:2:84", 4326, true)]
    [InlineData("CRS:84", 4326, true)]
    public async Task TryResolveGeometryCrsAsync_WithVersionedUrnsAndCrs84Aliases_ResolvesExpectedSrid(
        string crsName, int expectedSrid, bool isGeographic)
    {
        var registry = new SridOnlyCrsRegistry(
            expectedSrid,
            new CrsDefinition(
                $"http://www.opengis.net/def/crs/EPSG/0/{expectedSrid}",
                expectedSrid,
                isGeographic ? AxisOrder.NorthEast : AxisOrder.EastNorth,
                isGeographic));

        var geometry = new ODataSpatialGeometry
        {
            Type = "Point",
            CoordinatesJson = "[10,20]",
            Crs = new ODataSpatialCrs
            {
                Type = "name",
                Properties = new ODataSpatialCrsProperties
                {
                    Name = crsName
                }
            }
        };

        var result = await ODataCrsUtilities.TryResolveGeometryCrsAsync(registry, geometry, default);

        result.IsValid.Should().BeTrue(result.ErrorMessage);
        result.Definition.Should().NotBeNull();
        result.Definition!.Value.Srid.Should().Be(expectedSrid);
        result.Definition.Value.AxisOrder.Should().Be(AxisOrder.EastNorth);
    }

    private sealed class SridOnlyCrsRegistry(int supportedSrid, CrsDefinition definition) : ICrsRegistry
    {
        public ValueTask<CrsDefinition?> ResolveAsync(string? crsIdentifier, CancellationToken cancellationToken = default)
        {
            if (string.Equals(crsIdentifier, "http://www.opengis.net/def/crs/OGC/1.3/CRS84", StringComparison.OrdinalIgnoreCase) &&
                supportedSrid == 4326)
            {
                return ValueTask.FromResult<CrsDefinition?>(
                    new CrsDefinition("http://www.opengis.net/def/crs/OGC/1.3/CRS84", 4326, AxisOrder.EastNorth, true));
            }

            if (string.Equals(crsIdentifier, $"EPSG:{supportedSrid}", StringComparison.OrdinalIgnoreCase))
            {
                return ValueTask.FromResult<CrsDefinition?>(definition);
            }

            return ValueTask.FromResult<CrsDefinition?>(null);
        }

        public ValueTask<CrsDefinition?> ResolveBySridAsync(int srid, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(srid == supportedSrid ? (CrsDefinition?)definition : null);

        public ValueTask<bool> IsSridSupportedAsync(int srid, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(srid == supportedSrid);
    }

    private sealed class StubCrsRegistry(CrsDefinition? definition) : ICrsRegistry
    {
        private readonly CrsDefinition? _definition = definition;

        public ValueTask<CrsDefinition?> ResolveAsync(string? crsIdentifier, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_definition);

        public ValueTask<CrsDefinition?> ResolveBySridAsync(int srid, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_definition);

        public ValueTask<bool> IsSridSupportedAsync(int srid, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_definition.HasValue);
    }
}

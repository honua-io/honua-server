// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Protocols.OData.Models;
using Honua.Server.Features.Protocols.OData.Services;
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
        result.Definition!.Value.Uri.Should().Be("http://www.opengis.net/def/crs/EPSG/0/4326");
        result.Definition.Value.Srid.Should().Be(4326);
        result.Definition.Value.AxisOrder.Should().Be(AxisOrder.EastNorth);
        result.Definition.Value.IsGeographic.Should().BeTrue();
    }

    private sealed class StubCrsRegistry : ICrsRegistry
    {
        private readonly CrsDefinition? _definition;

        public StubCrsRegistry(CrsDefinition? definition)
        {
            _definition = definition;
        }

        public ValueTask<CrsDefinition?> ResolveAsync(string? crsIdentifier, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_definition);

        public ValueTask<CrsDefinition?> ResolveBySridAsync(int srid, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_definition);

        public ValueTask<bool> IsSridSupportedAsync(int srid, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_definition.HasValue);
    }
}

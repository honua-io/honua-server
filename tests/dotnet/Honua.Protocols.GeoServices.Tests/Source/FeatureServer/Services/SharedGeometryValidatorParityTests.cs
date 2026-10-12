// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.Core.Configuration;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Geometry.Domain;
using Honua.Infrastructure.Validation;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed class SharedGeometryValidatorParityTests
{
    [UnitTheory]
    [InlineData("POINT (1 2)")]
    [InlineData("LINESTRING (0 0, 1 1, 2 2)")]
    [InlineData("POLYGON ((0 0, 0 1, 1 1, 0 0))")]
    public async Task SharedWkbPipelineMatchesGeoServicesAdapter(string wkt)
    {
        var options = Options.Create(new LimitsOptions());
        var topology = Substitute.For<IGeometryTopologyValidator>();
        topology.ValidateTopologyAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(GeometryValidationResult.Success());
        var adapter = new GeometryValidator(options, topology, NullLogger<GeometryValidator>.Instance);
        var shared = new WkbGeometryValidator(options, topology, NullLogger.Instance);
        var bytes = new WKBWriter().Write(new WKTReader().Read(wkt));

        shared.ValidateWkb(bytes).IsValid.Should().BeTrue();
        shared.ValidateWkb([1, 2, 3]).IsValid.Should().BeFalse();
        shared.ValidateWkb([1, 2, 3]).Errors.Should().ContainSingle()
            .Which.Code.Should().Be(ValidationErrorCode.InvalidWkbStructure);
        adapter.ValidateWkb(bytes).Should().BeEquivalentTo(shared.ValidateWkb(bytes));
        (await adapter.ValidateCompleteAsync(bytes)).Should().BeEquivalentTo(await shared.ValidateCompleteAsync(bytes));
        adapter.ValidateWkb([1, 2, 3]).Should().BeEquivalentTo(shared.ValidateWkb([1, 2, 3]));
        adapter.ValidateWkb(null).Should().BeEquivalentTo(shared.ValidateWkb(null));
    }
}

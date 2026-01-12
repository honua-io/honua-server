// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Geometry.Domain;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.FeatureServer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.FeatureServer.Services;

public sealed class GeometryValidatorTests
{
    [Fact]
    public void ValidateEsriJson_RingClosureWithinTolerance_DoesNotWarn()
    {
        var options = Options.Create(new LimitsOptions
        {
            Validation = new GeometryValidationOptions
            {
                RingClosureTolerance = 1e-6
            }
        });
        var topologyValidator = Substitute.For<IGeometryTopologyValidator>();
        var validator = new GeometryValidator(options, topologyValidator, NullLogger<GeometryValidator>.Instance);

        var geometry = new GeoServicesGeometry
        {
            Rings = new[]
            {
                new[]
                {
                    new[] { 0.0, 0.0 },
                    new[] { 0.0, 1.0 },
                    new[] { 1.0, 1.0 },
                    new[] { 0.0000005, 0.0 }
                }
            }
        };

        var result = validator.ValidateEsriJson(geometry);

        result.IsValid.Should().BeTrue();
        result.Warnings.Should().NotContain(warning => warning.Code == ValidationErrorCode.UnclosedRing);
    }

    [Fact]
    public void ValidateEsriJson_RingClosureOutsideTolerance_Warns()
    {
        var options = Options.Create(new LimitsOptions
        {
            Validation = new GeometryValidationOptions
            {
                RingClosureTolerance = 1e-6
            }
        });
        var topologyValidator = Substitute.For<IGeometryTopologyValidator>();
        var validator = new GeometryValidator(options, topologyValidator, NullLogger<GeometryValidator>.Instance);

        var geometry = new GeoServicesGeometry
        {
            Rings = new[]
            {
                new[]
                {
                    new[] { 0.0, 0.0 },
                    new[] { 0.0, 1.0 },
                    new[] { 1.0, 1.0 },
                    new[] { 0.0001, 0.0 }
                }
            }
        };

        var result = validator.ValidateEsriJson(geometry);

        result.IsValid.Should().BeTrue();
        result.Warnings.Should().Contain(warning => warning.Code == ValidationErrorCode.UnclosedRing);
    }
}

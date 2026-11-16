// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Services;
using Honua.Infrastructure.GeoJson;
using Honua.Protocols.Ogc.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

[Trait("Category", "Unit")]
[Trait("Tier", "Fast")]
public sealed class OgcGeoJsonProjectionTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void Create_PublicIdentifier_RespectsPropertyProjection(bool encoded, bool prepared, bool numeric)
    {
        object publicIdentifier = numeric ? 701L : "park-7";
        var resource = new MetadataV2Object
        {
            SchemaFields =
            [
                new() { Name = "public_id", Type = numeric ? MetadataV2FieldType.BigInteger : MetadataV2FieldType.String, SemanticRoles = ["id.primary"] },
                new() { Name = "name", Type = MetadataV2FieldType.String }
            ]
        };
        var attributes = new Dictionary<string, object?> { ["public_id"] = publicIdentifier, ["name"] = "park" }
            .ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
        var geometry = new OgcFeaturesGeometryServices(
            new GeometryService(Options.Create(new LimitsOptions())),
            Substitute.For<ICoordinateTransformService>(),
            Options.Create(new LimitsOptions()));
        var schema = prepared ? new GeoJsonFeatureBaseBuilder.PreparedSchema(resource, false) : null;
        GeoJsonFeature Create(IReadOnlySet<string>? projection) => encoded
            ? OgcGeoJsonFeatureBuilder.Create(EncodedGeoJsonFeature.Create(7, null, attributes),
                resource, AxisOrder.EastNorth, geometry, projection, schema: schema)
            : OgcGeoJsonFeatureBuilder.Create(Feature.Create(7, null, attributes),
                resource, AxisOrder.EastNorth, geometry, projection, schema: schema);

        var projected = Create(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "name" });
        projected.Id.Should().Be(publicIdentifier);
        projected.Properties.Should().ContainSingle().Which.Key.Should().Be("name");
        var explicitIdentifier = Create(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "name", "PUBLIC_ID" });
        explicitIdentifier.Id.Should().Be(publicIdentifier);
        explicitIdentifier.Properties.Should().ContainKey("public_id").WhoseValue.Should().Be(publicIdentifier);
        Create(null).Properties.Should().ContainKey("public_id").WhoseValue.Should().Be(publicIdentifier);
    }
}

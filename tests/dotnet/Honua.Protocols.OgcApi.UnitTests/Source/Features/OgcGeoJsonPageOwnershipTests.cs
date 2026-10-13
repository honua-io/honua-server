// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Services;
using Honua.Protocols.Ogc.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

[Trait("Category", "Unit")]
[Trait("Tier", "Fast")]
public sealed class OgcGeoJsonPageOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_CanonicalFeatures_HaveIndependentResponseProperties(bool encoded)
    {
        var resource = new MetadataV2Resource
        {
            SchemaFields =
            [
                new() { Name = "objectid", Type = MetadataV2FieldType.Integer, SemanticRoles = ["id.primary"] },
                new() { Name = "name", Type = MetadataV2FieldType.String },
                new() { Name = "secret", Type = MetadataV2FieldType.String, Hidden = true }
            ]
        };
        var attributes = new Dictionary<string, object?> { ["name"] = "park", ["secret"] = "private" }
            .ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
        var geometry = new OgcFeaturesGeometryServices(
            new GeometryService(Options.Create(new LimitsOptions())),
            Substitute.For<ICoordinateTransformService>(),
            Options.Create(new LimitsOptions()),
            NullLogger<OgcFeaturesGeometryServices>.Instance);
        GeoJsonFeature Create() => encoded
            ? OgcGeoJsonFeatureBuilder.Create(EncodedGeoJsonFeature.Create(7, null, attributes), resource, AxisOrder.EastNorth, geometry)
            : OgcGeoJsonFeatureBuilder.Create(Feature.Create(7, null, attributes), resource, AxisOrder.EastNorth, geometry);

        var first = Create();
        var second = Create();
        first.Id.Should().Be(7L);
        first.Properties.Should().ContainKey("name").WhoseValue.Should().Be("park");
        first.Properties.Should().NotContainKey("secret");
        first.Properties["name"] = "changed";
        second.Properties["name"].Should().Be("park");
        attributes["name"].Should().Be("park");
    }

    [Theory]
    [InlineData(7)]
    [InlineData("public-id")]
    public void PublicAndArbitraryPropertiesConversions_RetainDetachedCopies(object id)
    {
        var properties = new Dictionary<string, object?> { ["name"] = "park" };
        var featureBase = GeoJsonFeatureBase.Create(id, properties, false);
        var converted = featureBase.ToOgcGeoJsonFeature();
        var built = OgcGeoJsonFeatureBuilder.Create(id, properties);
        properties["name"] = "changed";
        converted.Properties["name"].Should().Be("park");
        built.Properties["name"].Should().Be("park");
        converted.Id.Should().Be(id is int integer ? (object)(long)integer : id);
        built.Id.Should().Be(converted.Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Collection_ExplicitOwnershipPreservesCountsAndGenericConversionCopies(int count)
    {
        var features = Enumerable.Range(0, count)
            .Select(i => OgcGeoJsonFeatureBuilder.Create(i, new Dictionary<string, object?>()))
            .ToArray();
        var owned = OgcGeoJsonFeatureBuilder.CreateCollectionWithOwnedFeatures(features, 50);
        var copied = OgcGeoJsonFeatureBuilder.CreateCollection(features, 50);
        owned.Features.Should().BeSameAs(features);
        if (count > 0)
        {
            copied.Features.Should().NotBeSameAs(features);
        }
        owned.NumberReturned.Should().Be(count);
        owned.NumberMatched.Should().Be(50);
        owned.Features.Should().Equal(copied.Features);
        if (count > 0)
        {
            var original = copied.Features[0];
            features[0] = OgcGeoJsonFeatureBuilder.Create(99, new Dictionary<string, object?>());
            copied.Features[0].Should().BeSameAs(original);
        }
    }
}

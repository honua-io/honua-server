// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.ObjectModel;
using FluentAssertions;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Models;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class GeoJsonFeaturePropertyOwnershipTests
{
    [Fact]
    public void ToGeoJsonFeatureWithOwnedProperties_BuiltDictionary_IsTransferred()
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = "park", ["rank"] = 3L };
        var featureBase = GeoJsonFeatureBase.Create(7L, properties, hasGeometry: false);

        var owned = featureBase.ToGeoJsonFeatureWithOwnedProperties(geometry: null);
        var copied = featureBase.ToGeoJsonFeature();

        owned.Properties.Should().BeSameAs(properties);
        owned.Properties.Should().Equal(copied.Properties);
        owned.Id.Should().Be(copied.Id);
        owned.Type.Should().Be(copied.Type);
    }

    [Fact]
    public void ToGeoJsonFeatureWithOwnedProperties_ReadOnlyProperties_AreCopied()
    {
        var source = new Dictionary<string, object?> { ["name"] = "park" };
        var featureBase = GeoJsonFeatureBase.Create(7L, new ReadOnlyDictionary<string, object?>(source), hasGeometry: false);

        var feature = featureBase.ToGeoJsonFeatureWithOwnedProperties(geometry: null);
        source["name"] = "changed";

        feature.Properties["name"].Should().Be("park");
    }

    [Fact]
    public void ToGeoJsonFeature_PublicConversion_KeepsDetachedCopy()
    {
        var properties = new Dictionary<string, object?> { ["name"] = "park" };

        var feature = GeoJsonFeatureBase.Create(7L, properties, hasGeometry: false).ToGeoJsonFeature();
        properties["name"] = "changed";

        feature.Properties["name"].Should().Be("park");
    }
}

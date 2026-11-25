// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Protocols.OData.Models;
using Honua.Protocols.OData.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.OData.Services;

public sealed class ODataFeaturePayloadTests
{
    private static readonly int[] InnerValues = [1, 2];

    [UnitTest]
    public void BuildFeaturePayload_FromStoredAttributes_MatchesSerializedAttributePath()
    {
        using var json = JsonDocument.Parse("""{"a":[1,"two",{"b":null}]}""");
        var attributes = ImmutableDictionary<string, object?>.Empty
            .Add("name", "Main St")
            .Add("NAME", "shadowed by case")
            .Add("count", 42L)
            .Add("objectid", 9L)
            .Add("Geometry", "reserved")
            .Add(" ", "blank")
            .Add("json", json.RootElement.Clone())
            .Add("nested", new Dictionary<string, object?> { ["inner"] = InnerValues })
            .Add("missing", null);
        var feature = Feature.Create(5, geometry: null, attributes);
        var geometry = new ODataSpatialGeometry { Type = "Point", CoordinatesJson = "[1,2]" };

        var expected = ODataUtilityService.BuildFeaturePayload(new ODataUtilityService.ODataFeaturePayloadRequest
        {
            LayerId = 12,
            Feature = feature,
            Geometry = geometry,
            Attributes = ODataAttributeSerializer.Serialize(feature.Attributes),
        });
        var actual = ODataUtilityService.BuildFeaturePayload(12, feature, geometry);

        Assert.Equal(expected.Keys, actual.Keys);
        Assert.Same(geometry, actual["Geometry"]);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
        Assert.Equal(expected.Comparer, actual.Comparer);
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Catalog;

/// <summary>
/// Serialization round-trip tests for <see cref="LayerExtrusionInfo"/>
/// through the source-generated <see cref="CatalogJsonContext"/>. The
/// AOT integration depends on these types being registered in the
/// JSON context; without that registration, deserialization would
/// silently produce null values in published builds.
/// </summary>
[Protocol(Protocols.GeoservicesCatalog)]
public sealed class LayerExtrusionInfoSerializationTests
{
    [UnitTest]
    [Operation(Operations.Metadata)]
    public void RoundTrip_AllFields_PreservesValues()
    {
        var original = new LayerExtrusionInfo
        {
            HeightField = "height_m",
            BaseHeightField = "base_m",
            Unit = VerticalUnit.UsSurveyFeet,
            DefaultHeight = 4.5,
            MaterialHint = "concrete"
        };

        var json = JsonSerializer.Serialize(original, CatalogJsonContext.Default.LayerExtrusionInfo);
        var roundTrip = JsonSerializer.Deserialize(json, CatalogJsonContext.Default.LayerExtrusionInfo);

        roundTrip.Should().NotBeNull();
        roundTrip!.Should().Be(original);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Serialize_VerticalUnit_UsesCamelCaseStrings()
    {
        var meters = new LayerExtrusionInfo { HeightField = "h", Unit = VerticalUnit.Meters };
        var feet = new LayerExtrusionInfo { HeightField = "h", Unit = VerticalUnit.Feet };
        var usSurveyFeet = new LayerExtrusionInfo { HeightField = "h", Unit = VerticalUnit.UsSurveyFeet };

        var metersJson = JsonSerializer.Serialize(meters, CatalogJsonContext.Default.LayerExtrusionInfo);
        var feetJson = JsonSerializer.Serialize(feet, CatalogJsonContext.Default.LayerExtrusionInfo);
        var usSurveyFeetJson = JsonSerializer.Serialize(usSurveyFeet, CatalogJsonContext.Default.LayerExtrusionInfo);

        metersJson.Should().Contain("\"unit\":\"meters\"");
        feetJson.Should().Contain("\"unit\":\"feet\"");
        usSurveyFeetJson.Should().Contain("\"unit\":\"usSurveyFeet\"");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void CatalogMetadata_RoundTrip_PreservesExtrusion()
    {
        var original = new CatalogMetadata
        {
            Extrusion = new LayerExtrusionInfo
            {
                HeightField = "h",
                Unit = VerticalUnit.Feet,
                DefaultHeight = 0.0,
                MaterialHint = "glass"
            }
        };

        var json = JsonSerializer.Serialize(original, CatalogJsonContext.Default.CatalogMetadata);
        var roundTrip = JsonSerializer.Deserialize(json, CatalogJsonContext.Default.CatalogMetadata);

        roundTrip.Should().NotBeNull();
        roundTrip!.Extrusion.Should().NotBeNull();
        roundTrip.Extrusion.Should().Be(original.Extrusion);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void CatalogMetadata_DefaultValues_DeserializeAsNullExtrusion()
    {
        // A 2D-only catalog metadata payload (no extrusion property at all)
        // must deserialize to a null Extrusion property — the missing
        // property is the v1 contract's signal of "no 3D extrusion".
        const string payload = """{ "accessPolicy": null }""";

        var metadata = JsonSerializer.Deserialize(payload, CatalogJsonContext.Default.CatalogMetadata);

        metadata.Should().NotBeNull();
        metadata!.Extrusion.Should().BeNull();
    }
}

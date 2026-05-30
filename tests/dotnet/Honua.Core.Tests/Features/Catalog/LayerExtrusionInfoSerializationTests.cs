// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

/// <summary>
/// Serialization round-trip tests for <see cref="MetadataV2ExtrusionInfo"/>
/// through the source-generated <see cref="MetadataV2JsonContext"/>. The
/// AOT integration depends on these types being registered in the
/// JSON context; without that registration, deserialization would
/// silently produce null values in published builds.
/// </summary>
[Protocol(ProtocolNames.GeoservicesCatalog)]
public sealed class MetadataV2ExtrusionInfoSerializationTests
{
    [UnitTest]
    [Operation(Operations.Metadata)]
    public void RoundTrip_AllFields_PreservesValues()
    {
        var original = new MetadataV2ExtrusionInfo
        {
            HeightField = "height_m",
            BaseHeightField = "base_m",
            Unit = MetadataV2VerticalUnits.UsSurveyFeet,
            DefaultHeight = 4.5,
            MaterialHint = "concrete"
        };

        var json = JsonSerializer.Serialize(original, MetadataV2JsonContext.Default.MetadataV2ExtrusionInfo);
        var roundTrip = JsonSerializer.Deserialize(json, MetadataV2JsonContext.Default.MetadataV2ExtrusionInfo);

        roundTrip.Should().NotBeNull();
        roundTrip!.Should().Be(original);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Serialize_VerticalUnit_PreservesCanonicalTokens()
    {
        var meters = new MetadataV2ExtrusionInfo { HeightField = "h", Unit = MetadataV2VerticalUnits.Meters };
        var feet = new MetadataV2ExtrusionInfo { HeightField = "h", Unit = MetadataV2VerticalUnits.Feet };
        var usSurveyFeet = new MetadataV2ExtrusionInfo { HeightField = "h", Unit = MetadataV2VerticalUnits.UsSurveyFeet };

        var metersJson = JsonSerializer.Serialize(meters, MetadataV2JsonContext.Default.MetadataV2ExtrusionInfo);
        var feetJson = JsonSerializer.Serialize(feet, MetadataV2JsonContext.Default.MetadataV2ExtrusionInfo);
        var usSurveyFeetJson = JsonSerializer.Serialize(usSurveyFeet, MetadataV2JsonContext.Default.MetadataV2ExtrusionInfo);

        metersJson.Should().Contain("\"unit\":\"meters\"");
        feetJson.Should().Contain("\"unit\":\"feet\"");
        usSurveyFeetJson.Should().Contain("\"unit\":\"usSurveyFeet\"");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Deserialize_UnknownUnit_DoesNotThrow()
    {
        // Unknown unit tokens must round-trip into the graph model so the validator can
        // surface them as EXTRUSION_UNIT_UNRECOGNIZED rather than failing during deserialization.
        const string payload = """{"heightField":"h","unit":"yards"}""";

        var deserialized = JsonSerializer.Deserialize(payload, MetadataV2JsonContext.Default.MetadataV2ExtrusionInfo);

        deserialized.Should().NotBeNull();
        deserialized!.Unit.Should().Be("yards");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Resource_RoundTrip_PreservesExtrusion()
    {
        var original = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-1", Name = "buildings" },
            Extrusion = new MetadataV2ExtrusionInfo
            {
                HeightField = "h",
                Unit = MetadataV2VerticalUnits.Feet,
                DefaultHeight = 0.0,
                MaterialHint = "glass"
            }
        };

        var json = JsonSerializer.Serialize(original, MetadataV2JsonContext.Default.MetadataV2Resource);
        var roundTrip = JsonSerializer.Deserialize(json, MetadataV2JsonContext.Default.MetadataV2Resource);

        roundTrip.Should().NotBeNull();
        roundTrip!.Extrusion.Should().NotBeNull();
        roundTrip.Extrusion.Should().Be(original.Extrusion);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Resource_DefaultValues_DeserializeAsNullExtrusion()
    {
        // A 2D-only resource payload (no extrusion property at all) must deserialize to
        // null; the missing property is the Metadata v2 signal of "no 3D extrusion".
        const string payload = """{"metadata":{"id":"resource-1","name":"buildings"}}""";

        var resource = JsonSerializer.Deserialize(payload, MetadataV2JsonContext.Default.MetadataV2Resource);

        resource.Should().NotBeNull();
        resource!.Extrusion.Should().BeNull();
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// Pins the FeatureServer layer descriptor's <c>hasZ</c>/<c>hasM</c> advertisement (#1877 Part C):
/// the flags are emitted when authored true, and omitted (CITE/byte-stable) when false/default so
/// 2D layer documents stay identical to prior behavior.
/// </summary>
public sealed class LayerResponseHasZMSerializationTests
{
    private static LayerResponse BuildLayer(bool hasZ, bool hasM) => new()
    {
        Id = 0,
        Name = "test",
        GeometryType = "esriGeometryPoint",
        SpatialReference = new SpatialReferenceInfo { Wkid = 4326, LatestWkid = 4326 },
        Fields = [],
        ObjectIdField = "objectid",
        HasZ = hasZ,
        HasM = hasM,
    };

    [Fact]
    public void Serialize_WhenHasZAndHasMTrue_EmitsBothFlags()
    {
        var json = JsonSerializer.Serialize(BuildLayer(hasZ: true, hasM: true), FeatureServerJsonContext.Default.LayerResponse);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("hasZ").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("hasM").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Serialize_WhenHasZAndHasMFalse_OmitsBothFlags()
    {
        var json = JsonSerializer.Serialize(BuildLayer(hasZ: false, hasM: false), FeatureServerJsonContext.Default.LayerResponse);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("hasZ", out _).Should().BeFalse(
            "2D layer documents must omit hasZ to stay byte-stable for existing clients");
        doc.RootElement.TryGetProperty("hasM", out _).Should().BeFalse(
            "non-measured layer documents must omit hasM to stay byte-stable for existing clients");
    }

    [Fact]
    public void Serialize_WhenOnlyHasZTrue_EmitsHasZOnly()
    {
        var json = JsonSerializer.Serialize(BuildLayer(hasZ: true, hasM: false), FeatureServerJsonContext.Default.LayerResponse);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("hasZ").GetBoolean().Should().BeTrue();
        doc.RootElement.TryGetProperty("hasM", out _).Should().BeFalse();
    }
}

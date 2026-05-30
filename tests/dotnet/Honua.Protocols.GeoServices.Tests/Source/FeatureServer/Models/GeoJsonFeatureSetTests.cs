// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Models;

public sealed class GeoJsonFeatureSetTests
{
    [Fact]
    public void Serialize_DoesNotEmitNamedCrsMembers()
    {
        var payload = new GeoJsonFeatureSet
        {
            Features =
            [
                new GeoJsonFeature
                {
                    Id = 1,
                    Properties = new Dictionary<string, object?> { ["name"] = "Test" },
                    Geometry = new GeoJsonGeometry
                    {
                        Type = "Point",
                        Coordinates = new[] { -122.4194, 37.7749 }
                    }
                }
            ]
        };

        var json = JsonSerializer.Serialize(payload, FeatureServerJsonContext.Default.GeoJsonFeatureSet);
        using var document = JsonDocument.Parse(json);

        document.RootElement.TryGetProperty("crs", out _).Should().BeFalse();
        document.RootElement.GetProperty("features")[0].GetProperty("geometry").TryGetProperty("crs", out _).Should().BeFalse();
    }
}

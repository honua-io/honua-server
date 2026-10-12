// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Models;

public sealed class QueryResponseSerializationTests
{
    [Fact]
    public void Serialize_WithExtentOnly_IncludesExtentPayload()
    {
        var payload = new QueryResponse
        {
            Extent = new ExtentInfo
            {
                Xmin = -158.2,
                Ymin = 21.2,
                Xmax = -157.8,
                Ymax = 21.6,
                SpatialReference = new SpatialReferenceInfo
                {
                    Wkid = 4326
                }
            }
        };

        var json = JsonSerializer.Serialize(payload, FeatureServerJsonContext.Default.QueryResponse);
        using var document = JsonDocument.Parse(json);

        document.RootElement.TryGetProperty("extent", out var extent).Should().BeTrue();
        extent.GetProperty("xmin").GetDouble().Should().Be(-158.2);
        extent.GetProperty("ymin").GetDouble().Should().Be(21.2);
        extent.GetProperty("xmax").GetDouble().Should().Be(-157.8);
        extent.GetProperty("ymax").GetDouble().Should().Be(21.6);
        extent.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);
        document.RootElement.TryGetProperty("features", out _).Should().BeFalse();
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// The GeoServices spec defines <c>supportedQueryFormats</c> as a comma-delimited
/// string. The ArcGIS Maps SDK for JavaScript parses it with <c>value.split(",")</c>,
/// so emitting it as a JSON array broke <c>FeatureLayer.load()</c> ("split is not a
/// function") and prevented the JS SDK from loading any FeatureServer layer. These
/// tests pin the spec-compliant string wire format while preserving the lenient
/// (string-or-array) read path the internal <c>string[]</c> contract relies on.
/// </summary>
public sealed class SupportedQueryFormatsSerializationTests
{
    [Fact]
    public void Serialize_Layer_EmitsSupportedQueryFormatsAsCommaDelimitedString()
    {
        var layer = new LayerResponse { SupportedQueryFormats = ["JSON", "PBF", "FGB"] };

        var json = JsonSerializer.Serialize(layer);

        using var doc = JsonDocument.Parse(json);
        var prop = doc.RootElement.GetProperty("supportedQueryFormats");
        prop.ValueKind.Should().Be(JsonValueKind.String, "the GeoServices spec defines it as a comma-delimited string");
        prop.GetString().Should().Be("JSON, PBF, FGB");
    }

    [Fact]
    public void Serialize_Service_EmitsSupportedQueryFormatsAsCommaDelimitedString()
    {
        var service = new FeatureServerResponse { SupportedQueryFormats = ["JSON", "GeoJSON", "PBF"] };

        var json = JsonSerializer.Serialize(service);

        using var doc = JsonDocument.Parse(json);
        var prop = doc.RootElement.GetProperty("supportedQueryFormats");
        prop.ValueKind.Should().Be(JsonValueKind.String);
        prop.GetString().Should().Be("JSON, GeoJSON, PBF");
    }

    [Fact]
    public void Deserialize_FromCommaDelimitedString_RoundTripsToArray()
    {
        const string json = """{"supportedQueryFormats":"JSON, PBF, FGB"}""";

        var layer = JsonSerializer.Deserialize<LayerResponse>(json);

        layer!.SupportedQueryFormats.Should().BeEquivalentTo("JSON", "PBF", "FGB");
    }

    [Fact]
    public void Deserialize_FromJsonArray_StillAccepted()
    {
        // The read path stays lenient so any legacy array payload still round-trips.
        const string json = """{"supportedQueryFormats":["JSON","PBF"]}""";

        var layer = JsonSerializer.Deserialize<LayerResponse>(json);

        layer!.SupportedQueryFormats.Should().BeEquivalentTo("JSON", "PBF");
    }
}

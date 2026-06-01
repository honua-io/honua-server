// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// The GeoServices spec defines <c>supportedQueryFormats</c> as a comma-delimited
/// string. The ArcGIS Maps SDK for JavaScript parses it with <c>value.split(",")</c>,
/// so emitting it as a JSON array broke <c>FeatureLayer.load()</c> ("split is not a
/// function") and prevented the JS SDK from loading any FeatureServer layer. These
/// tests pin the spec-compliant string wire format while preserving the lenient
/// (string-or-array) read path the internal <c>string[]</c> contract relies on, and
/// verify the response DTOs apply the converter.
/// </summary>
public sealed class SupportedQueryFormatsSerializationTests
{
    private sealed record Holder
    {
        [JsonConverter(typeof(CommaDelimitedStringArrayConverter))]
        public string[] Formats { get; init; } = [];
    }

    [Fact]
    public void Serialize_EmitsCommaDelimitedString()
    {
        var json = JsonSerializer.Serialize(new Holder { Formats = ["JSON", "PBF", "FGB"] });

        using var doc = JsonDocument.Parse(json);
        var prop = doc.RootElement.GetProperty("Formats");
        prop.ValueKind.Should().Be(JsonValueKind.String, "the GeoServices spec defines it as a comma-delimited string");
        prop.GetString().Should().Be("JSON, PBF, FGB");
    }

    [Fact]
    public void Deserialize_FromCommaDelimitedString_RoundTripsToArray()
    {
        var holder = JsonSerializer.Deserialize<Holder>("""{"Formats":"JSON, PBF, FGB"}""");

        holder!.Formats.Should().BeEquivalentTo("JSON", "PBF", "FGB");
    }

    [Fact]
    public void Deserialize_FromJsonArray_StillAccepted()
    {
        // The read path stays lenient so any legacy array payload still round-trips.
        var holder = JsonSerializer.Deserialize<Holder>("""{"Formats":["JSON","PBF"]}""");

        holder!.Formats.Should().BeEquivalentTo("JSON", "PBF");
    }

    [Fact]
    public void Serialize_EmptyArray_EmitsEmptyString()
    {
        var json = JsonSerializer.Serialize(new Holder { Formats = [] });

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("Formats").GetString().Should().BeEmpty();
    }

    [Theory]
    [InlineData(typeof(LayerResponse))]
    [InlineData(typeof(FeatureServerResponse))]
    public void ResponseDto_AppliesCommaDelimitedConverter(Type dtoType)
    {
        var property = dtoType.GetProperty("SupportedQueryFormats");
        property.Should().NotBeNull();

        var attribute = property!.GetCustomAttribute<JsonConverterAttribute>();
        attribute.Should().NotBeNull("the FeatureServer metadata DTOs must emit supportedQueryFormats as a spec-compliant string");
        attribute!.ConverterType.Should().Be<CommaDelimitedStringArrayConverter>();
    }
}

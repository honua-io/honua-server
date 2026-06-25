// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Geoprocessing;

/// <summary>
/// Unit tests for <see cref="ProcessSchemaJsonGenerator"/> — the GP Devkit authoring
/// contract's JSON Schema emission derived from a process's typed parameter set
/// (issues #2122 / #2124). Pins the JSON Schema shape <c>describe</c> consumes so the
/// advertised schema cannot silently drift from the catalog typing.
/// </summary>
[Protocol(ProtocolNames.TestQuality)]
public sealed class ProcessSchemaJsonGeneratorTests
{
    private static ProcessDefinition SampleProcess() => new()
    {
        ProcessId = "geometry.buffer",
        Title = "Buffer",
        Description = "Buffers a geometry by a distance.",
        Category = "geometry",
        RuntimeProfile = "managed",
        Parameters = new[]
        {
            new ProcessParameterSpec
            {
                Name = "wkb",
                DisplayName = "Geometry (WKB)",
                Description = "Input geometry as base64 WKB.",
                ValueType = ProcessParameterValueType.Wkb,
                Required = true,
            },
            new ProcessParameterSpec
            {
                Name = "srid",
                DisplayName = "SRID",
                Description = "Spatial reference identifier.",
                ValueType = ProcessParameterValueType.Srid,
                Required = true,
            },
            new ProcessParameterSpec
            {
                Name = "distance",
                DisplayName = "Distance",
                Description = "Buffer distance in CRS units.",
                ValueType = ProcessParameterValueType.FloatingPoint,
                Required = true,
            },
            new ProcessParameterSpec
            {
                Name = "geodesic",
                DisplayName = "Geodesic",
                Description = "Whether to buffer geodesically.",
                ValueType = ProcessParameterValueType.Flag,
                Required = false,
                DefaultValue = "false",
            },
            new ProcessParameterSpec
            {
                Name = "method",
                DisplayName = "Method",
                Description = "Join method.",
                ValueType = ProcessParameterValueType.Text,
                Required = false,
                AllowedValues = new[] { "round", "mitre", "bevel" },
            },
        },
        OutputArtifactKinds = new[] { ArtifactKind.FeatureLayer },
    };

    [UnitTest]
    public void Generate_ProducesValidJsonWithProcessMetadata()
    {
        var json = ProcessSchemaJsonGenerator.Generate(SampleProcess());

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("processId").GetString().Should().Be("geometry.buffer");
        root.GetProperty("title").GetString().Should().Be("Buffer");
        root.GetProperty("category").GetString().Should().Be("geometry");
        root.GetProperty("runtimeProfile").GetString().Should().Be("managed");
    }

    [UnitTest]
    public void Generate_RequiredArrayContainsOnlyRequiredParameters()
    {
        var json = ProcessSchemaJsonGenerator.Generate(SampleProcess());

        using var doc = JsonDocument.Parse(json);
        var inputs = doc.RootElement.GetProperty("inputs");

        inputs.GetProperty("type").GetString().Should().Be("object");
        inputs.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();

        var required = inputs.GetProperty("required")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();

        required.Should().BeEquivalentTo("wkb", "srid", "distance");
        required.Should().NotContain("geodesic");
        required.Should().NotContain("method");
    }

    [UnitTest]
    public void Generate_MapsValueTypesToJsonSchemaTypesAndFormats()
    {
        var json = ProcessSchemaJsonGenerator.Generate(SampleProcess());

        using var doc = JsonDocument.Parse(json);
        var props = doc.RootElement.GetProperty("inputs").GetProperty("properties");

        var wkb = props.GetProperty("wkb");
        wkb.GetProperty("type").GetString().Should().Be("string");
        wkb.GetProperty("format").GetString().Should().Be("wkb-base64");
        wkb.GetProperty("x-honua-value-type").GetString().Should().Be("Wkb");

        props.GetProperty("srid").GetProperty("type").GetString().Should().Be("integer");
        props.GetProperty("srid").GetProperty("format").GetString().Should().Be("srid");

        props.GetProperty("distance").GetProperty("type").GetString().Should().Be("number");
        props.GetProperty("geodesic").GetProperty("type").GetString().Should().Be("boolean");
        props.GetProperty("geodesic").GetProperty("default").GetString().Should().Be("false");
    }

    [UnitTest]
    public void Generate_EmitsEnumForAllowedValues()
    {
        var json = ProcessSchemaJsonGenerator.Generate(SampleProcess());

        using var doc = JsonDocument.Parse(json);
        var method = doc.RootElement.GetProperty("inputs").GetProperty("properties").GetProperty("method");

        var values = method.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToArray();
        values.Should().BeEquivalentTo("round", "mitre", "bevel");
    }

    [UnitTest]
    public void Generate_EmitsOutputArtifactKinds()
    {
        var json = ProcessSchemaJsonGenerator.Generate(SampleProcess());

        using var doc = JsonDocument.Parse(json);
        var outputs = doc.RootElement.GetProperty("outputs").EnumerateArray().ToArray();

        outputs.Should().HaveCount(1);
        outputs[0].GetProperty("artifactKind").GetString().Should().Be("FeatureLayer");
    }
}

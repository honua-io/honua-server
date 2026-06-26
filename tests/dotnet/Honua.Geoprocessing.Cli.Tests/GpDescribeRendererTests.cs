// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing.Cli;
using Xunit;

namespace Honua.Geoprocessing.Cli.Tests;

/// <summary>
/// Offline unit tests for the <c>honua gp describe</c> renderer (GP Devkit P3, issue #2124):
/// the typed parameter view, the inputs JSON Schema, and the machine-readable descriptor.
/// Pure string/JSON transforms over a hand-built <see cref="ProcessDefinition"/>; no Redis,
/// catalog, or control plane.
/// </summary>
public sealed class GpDescribeRendererTests
{
    private static ProcessDefinition SampleDefinition() => new()
    {
        ProcessId = "geometry.buffer",
        Title = "Buffer",
        Description = "Expands a geometry by a fixed distance.",
        Category = "geometry",
        RuntimeProfile = RuntimeProfiles.Managed,
        Parameters =
        [
            new ProcessParameterSpec
            {
                Name = "wkb",
                DisplayName = "Geometry",
                Description = "The input geometry as base64 WKB.",
                ValueType = ProcessParameterValueType.Wkb,
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
                Name = "endCap",
                DisplayName = "End cap style",
                Description = "Cap style for line ends.",
                ValueType = ProcessParameterValueType.Text,
                Required = false,
                DefaultValue = "round",
                AllowedValues = ["round", "flat", "square"],
            },
        ],
        OutputArtifactKinds = [ArtifactKind.FeatureLayer],
    };

    [Fact]
    public void RenderText_IncludesTypedParametersOutputsAndExample()
    {
        var text = GpDescribeRenderer.RenderText(SampleDefinition());

        text.Should().Contain("process     : geometry.buffer");
        text.Should().Contain("runtime     : managed");
        text.Should().Contain("wkb [string, required]");
        text.Should().Contain("distance [number, required]");
        text.Should().Contain("endCap [string, optional] = round");
        text.Should().Contain("allowed: round, flat, square");
        text.Should().Contain("outputs     : FeatureLayer");
        // The example invocation only carries the required parameters.
        text.Should().Contain("honua gp run geometry.buffer --param wkb=");
        text.Should().Contain("--param distance=0.0");
        text.Should().NotContain("--param endCap");
    }

    [Fact]
    public void RenderJson_ProducesValidInputSchemaWithRequiredAndEnum()
    {
        var json = GpDescribeRenderer.RenderJson(SampleDefinition());

        var root = JsonNode.Parse(json)!.AsObject();
        root["id"]!.GetValue<string>().Should().Be("geometry.buffer");
        root["runtimeProfile"]!.GetValue<string>().Should().Be("managed");

        var inputs = root["inputs"]!.AsObject();
        inputs["type"]!.GetValue<string>().Should().Be("object");

        var properties = inputs["properties"]!.AsObject();
        properties["distance"]!["type"]!.GetValue<string>().Should().Be("number");
        properties["wkb"]!["type"]!.GetValue<string>().Should().Be("string");

        var endCapEnum = properties["endCap"]!["enum"]!.AsArray()
            .Select(node => node!.GetValue<string>());
        endCapEnum.Should().Equal("round", "flat", "square");
        properties["endCap"]!["default"]!.GetValue<string>().Should().Be("round");

        var required = inputs["required"]!.AsArray().Select(node => node!.GetValue<string>());
        required.Should().Contain("wkb").And.Contain("distance");
        required.Should().NotContain("endCap");

        root["outputs"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("FeatureLayer");
    }

    [Fact]
    public void RenderText_NoParameters_RendersNonePlaceholders()
    {
        var definition = SampleDefinition() with
        {
            Parameters = [],
            OutputArtifactKinds = [],
        };

        var text = GpDescribeRenderer.RenderText(definition);

        text.Should().Contain("parameters:");
        text.Should().Contain("  (none)");
        text.Should().Contain("outputs     : (none)");
    }

    [Fact]
    public void RenderJson_ArrayParameter_DeclaresItemsType()
    {
        var definition = SampleDefinition() with
        {
            Parameters =
            [
                new ProcessParameterSpec
                {
                    Name = "geometries",
                    DisplayName = "Geometries",
                    Description = "Input geometries as base64 WKB.",
                    ValueType = ProcessParameterValueType.WkbArray,
                    Required = true,
                },
            ],
        };

        var json = GpDescribeRenderer.RenderJson(definition);
        var properties = JsonNode.Parse(json)!["inputs"]!["properties"]!.AsObject();

        properties["geometries"]!["type"]!.GetValue<string>().Should().Be("array");
        properties["geometries"]!["items"]!["type"]!.GetValue<string>().Should().Be("string");
    }
}

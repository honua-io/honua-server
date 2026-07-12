// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Raster;

public sealed class InlineColorRampTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [UnitTest]
    public void Resolve_AlgorithmicRamp_ProducesStopsSpanningDisplayRange()
    {
        var element = Parse(
            """{"type":"algorithmic","fromColor":[0,0,0],"toColor":[255,255,255],"algorithm":"esriHSVAlgorithm"}""");

        var resolution = InlineColorRamp.Resolve(element);

        resolution.Status.Should().Be(InlineColorRampStatus.Resolved);
        resolution.Colormap.Should().NotBeNull();
        resolution.Colormap!.Entries.Should().HaveCountGreaterThan(1);
        resolution.Colormap.Entries[0].Value.Should().Be(0);
        resolution.Colormap.Entries[^1].Value.Should().Be(255);
        resolution.Colormap.Entries.Should().BeInAscendingOrder(static e => e.Value);
    }

    [UnitTest]
    public void Resolve_AlgorithmicRamp_ReflectsEndpointColours()
    {
        var element = Parse(
            """{"type":"algorithmic","fromColor":[10,20,30,128],"toColor":[200,210,220],"algorithm":"esriCIELabAlgorithm"}""");

        var resolution = InlineColorRamp.Resolve(element);

        resolution.Status.Should().Be(InlineColorRampStatus.Resolved);
        var first = resolution.Colormap!.Entries[0];
        (first.Red, first.Green, first.Blue, first.Alpha).Should().Be(((byte)10, (byte)20, (byte)30, (byte)128));
        var last = resolution.Colormap.Entries[^1];
        (last.Red, last.Green, last.Blue, last.Alpha).Should().Be(((byte)200, (byte)210, (byte)220, (byte)255));
    }

    [UnitTest]
    public void Resolve_AlgorithmicRamp_DefaultsAlgorithmWhenOmitted()
    {
        var element = Parse("""{"type":"algorithmic","fromColor":[0,0,0],"toColor":[255,255,255]}""");

        var resolution = InlineColorRamp.Resolve(element);

        resolution.Status.Should().Be(InlineColorRampStatus.Resolved);
    }

    [UnitTest]
    public void Resolve_MultipartRamp_ConcatenatesSegmentsAcrossRange()
    {
        var element = Parse(
            """{"type":"multipart","colorRamps":[{"type":"algorithmic","fromColor":[0,0,0],"toColor":[255,0,0]},{"type":"algorithmic","fromColor":[255,0,0],"toColor":[255,255,0]}]}""");

        var resolution = InlineColorRamp.Resolve(element);

        resolution.Status.Should().Be(InlineColorRampStatus.Resolved);
        resolution.Colormap!.Entries[0].Value.Should().Be(0);
        resolution.Colormap.Entries[^1].Value.Should().Be(255);
        resolution.Colormap.Entries.Should().BeInAscendingOrder(static e => e.Value);
        // The first segment ends on red; a stop near the midpoint should carry the shared boundary.
        var midpoint = resolution.Colormap.Entries.First(e => e.Value >= 127);
        midpoint.Red.Should().BeGreaterThan(midpoint.Green);
    }

    [UnitTest]
    public void Resolve_RandomRamp_IsUnsupported()
    {
        var element = Parse("""{"type":"random","fromColor":[0,0,0],"toColor":[255,255,255]}""");

        var resolution = InlineColorRamp.Resolve(element);

        resolution.Status.Should().Be(InlineColorRampStatus.Unsupported);
        resolution.Error.Should().Contain("random");
    }

    [UnitTest]
    public void Resolve_UnknownAlgorithm_IsUnsupported()
    {
        var element = Parse(
            """{"type":"algorithmic","fromColor":[0,0,0],"toColor":[255,255,255],"algorithm":"esriNopeAlgorithm"}""");

        var resolution = InlineColorRamp.Resolve(element);

        resolution.Status.Should().Be(InlineColorRampStatus.Unsupported);
        resolution.Error.Should().Contain("algorithm");
    }

    [UnitTest]
    public void Resolve_UnknownType_IsInvalid()
    {
        var element = Parse("""{"type":"bogus","fromColor":[0,0,0],"toColor":[255,255,255]}""");

        var resolution = InlineColorRamp.Resolve(element);

        resolution.Status.Should().Be(InlineColorRampStatus.Invalid);
        resolution.Error.Should().Contain("type");
    }

    [UnitTest]
    public void Resolve_MissingToColor_IsInvalid()
    {
        var element = Parse("""{"type":"algorithmic","fromColor":[0,0,0]}""");

        var resolution = InlineColorRamp.Resolve(element);

        resolution.Status.Should().Be(InlineColorRampStatus.Invalid);
        resolution.Error.Should().Contain("toColor");
    }

    [UnitTest]
    public void Resolve_MultipartWithNonAlgorithmicPart_IsInvalid()
    {
        var element = Parse(
            """{"type":"multipart","colorRamps":[{"type":"random","fromColor":[0,0,0],"toColor":[255,0,0]}]}""");

        var resolution = InlineColorRamp.Resolve(element);

        resolution.Status.Should().Be(InlineColorRampStatus.Invalid);
    }

    [UnitTest]
    public void Resolve_MultipartWithEmptyColorRamps_IsInvalid()
    {
        var element = Parse("""{"type":"multipart","colorRamps":[]}""");

        var resolution = InlineColorRamp.Resolve(element);

        resolution.Status.Should().Be(InlineColorRampStatus.Invalid);
    }

    [UnitTest]
    public void Resolve_NonObject_IsInvalid()
    {
        var element = Parse("""[1,2,3]""");

        var resolution = InlineColorRamp.Resolve(element);

        resolution.Status.Should().Be(InlineColorRampStatus.Invalid);
    }
}

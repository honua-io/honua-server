// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Validation;
using Honua.Infrastructure.Validation;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed class LayerDefsParserTests
{
    private static readonly CommonQueryValidator Validator =
        new(Options.Create(new LimitsOptions()));

    // Regression (#1430): the Esri JSON-array layerDefs form
    // [{"layerId":N,"where":"..."}] must parse into the same per-layer map as the
    // object form, instead of failing with "Invalid layer id".
    [Fact]
    public void TryParse_WithJsonArrayForm_ParsesPerLayerDefinitions()
    {
        var ok = LayerDefsParser.TryParse(
            """[{"layerId":0,"where":"category = 'test'"},{"layerId":2,"where":"pop > 100"}]""",
            Validator,
            out var layerDefs,
            out var error);

        ok.Should().BeTrue(error);
        error.Should().BeNull();
        layerDefs.Should().HaveCount(2);
        layerDefs[0].Should().Be("category = 'test'");
        layerDefs[2].Should().Be("pop > 100");
    }

    [Fact]
    public void TryParse_WithJsonArrayForm_AllowsNullAndMissingWhere()
    {
        var ok = LayerDefsParser.TryParse(
            """[{"layerId":0,"where":null},{"layerId":1}]""",
            Validator,
            out var layerDefs,
            out var error);

        ok.Should().BeTrue(error);
        layerDefs.Should().HaveCount(2);
        layerDefs[0].Should().BeNull();
        layerDefs[1].Should().BeNull();
    }

    [Fact]
    public void TryParse_WithJsonArrayMissingLayerId_ReturnsError()
    {
        var ok = LayerDefsParser.TryParse(
            """[{"where":"category = 'test'"}]""",
            Validator,
            out _,
            out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNull();
    }

    // The existing object form must keep working.
    [Fact]
    public void TryParse_WithJsonObjectForm_StillParses()
    {
        var ok = LayerDefsParser.TryParse(
            """{"0":"category = 'test'"}""",
            Validator,
            out var layerDefs,
            out var error);

        ok.Should().BeTrue(error);
        layerDefs[0].Should().Be("category = 'test'");
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Rendering;
using Honua.Server.Features.Styling.Sld;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

[Trait("Component", "Admin")]
public sealed class SldExportEmptySymbolTests
{
    [UnitTest]
    public void Export_OnlySymbolLayerWithNothingExportable_ReportsNoConvertibleLayers()
    {
        var export = MapLibreToSldConverter.Export(new[] { DataDrivenIconLayer() }, "test");

        export.Diagnostics.Should().Contain(d =>
            d.Severity == SldDiagnosticSeverity.Error && d.Construct == "MapLibreLayers");
        export.SldXml.Should().NotContain("<Rule>");
    }

    [UnitTest]
    public void Export_SymbolLayerWithNothingExportable_IsDroppedBesideExportableLayer()
    {
        var fill = new MapLibreStyleLayer
        {
            Id = "area",
            Type = "fill",
            Paint = new Dictionary<string, MapLibreExpression>
            {
                ["fill-color"] = new MapLibreExpression("#336699")
            }
        };

        var export = MapLibreToSldConverter.Export(new[] { fill, DataDrivenIconLayer() }, "test");

        export.Diagnostics.Should().NotContain(d => d.Severity == SldDiagnosticSeverity.Error);
        export.Diagnostics.Should().Contain(d => d.Construct == "SymbolLayer");
        export.SldXml.Should().Contain("<Name>area</Name>");
        export.SldXml.Should().NotContain("<Name>ic</Name>");
    }

    private static MapLibreStyleLayer DataDrivenIconLayer() => new()
    {
        Id = "ic",
        Type = "symbol",
        Layout = new Dictionary<string, MapLibreExpression>
        {
            ["icon-image"] = new MapLibreExpression(new[]
            {
                new MapLibreExpression("match"),
                new MapLibreExpression(new[] { new MapLibreExpression("get"), new MapLibreExpression("kind") }),
                new MapLibreExpression("park"),
                new MapLibreExpression("a"),
                new MapLibreExpression("b")
            })
        }
    };
}

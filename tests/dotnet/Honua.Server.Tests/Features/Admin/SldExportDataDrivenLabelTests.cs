// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Rendering;
using Honua.Server.Features.Styling.Sld;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

// SLD export writes the data-driven text-field ["get", field] as <ogc:PropertyName>, so an SLD
// imported and exported again keeps its labels, and never writes a TextSymbolizer without a Label.
[Trait("Component", "Admin")]
public sealed class SldExportDataDrivenLabelTests
{
    [UnitTest]
    public void Export_GetTextField_WritesPropertyNameLabel()
    {
        var export = MapLibreToSldConverter.Export(new[] { TextLayer(Get("name")) }, "test");

        export.SldXml.Should().Contain("<ogc:PropertyName>name</ogc:PropertyName>");
        export.Diagnostics.Should().NotContain(d => d.Construct == "text-field");
    }

    [UnitTest]
    public void Export_ToStringOfGetTextField_WritesPropertyNameLabel()
    {
        var toString = new MapLibreExpression(new[] { new MapLibreExpression("to-string"), Get("height") });

        var export = MapLibreToSldConverter.Export(new[] { TextLayer(toString) }, "test");

        export.SldXml.Should().Contain("<ogc:PropertyName>height</ogc:PropertyName>");
    }

    [UnitTest]
    public void Export_InexpressibleTextField_WritesNoEmptyTextSymbolizer()
    {
        var concat = new MapLibreExpression(new[] { new MapLibreExpression("concat"), Get("a"), Get("b") });

        var export = MapLibreToSldConverter.Export(new[] { TextLayer(concat) }, "test");

        export.SldXml.Should().NotContain("TextSymbolizer");
        export.Diagnostics.Should().Contain(d => d.Construct == "text-field");
        export.Diagnostics.Should().Contain(d =>
            d.Severity == SldDiagnosticSeverity.Error && d.Construct == "MapLibreLayers");
    }

    [UnitTest]
    public void Export_GetOfEmptyField_WritesNoEmptyPropertyName()
    {
        var export = MapLibreToSldConverter.Export(new[] { TextLayer(Get(string.Empty)) }, "test");

        export.SldXml.Should().NotContain("PropertyName");
        export.Diagnostics.Should().Contain(d => d.Construct == "text-field");
    }

    [UnitTest]
    public void Export_LiteralTemplateTextField_StillWritesPropertyNameLabel()
    {
        var export = MapLibreToSldConverter.Export(new[] { TextLayer(new MapLibreExpression("{name}")) }, "test");

        export.SldXml.Should().Contain("<ogc:PropertyName>name</ogc:PropertyName>");
        export.SldXml.Should().Contain("font-size");
    }

    private static MapLibreExpression Get(string field)
        => new(new[] { new MapLibreExpression("get"), new MapLibreExpression(field) });

    private static MapLibreStyleLayer TextLayer(MapLibreExpression textField) => new()
    {
        Id = "sym3",
        Type = "symbol",
        Layout = new Dictionary<string, MapLibreExpression>
        {
            ["text-field"] = textField,
            ["text-size"] = new MapLibreExpression(12),
        },
    };
}

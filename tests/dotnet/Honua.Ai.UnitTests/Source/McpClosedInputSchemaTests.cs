// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

// A tool whose input schema is open silently drops an argument name it does not know, so a
// misspelled filter returns an answer the caller did not ask for. Every hand-written
// tool closes its input schema unless it is listed here with the reason it stays open.
public sealed partial class McpTaxonomyAlignmentTests
{
    private static readonly Dictionary<string, string> OpenInputSchemaReasons =
        new(StringComparer.Ordinal)
        {
            ["honua_publish_result"] =
                "The geospatial-mcp publish_result schema admits extension members; Honua reads only the declared ones.",
            ["honua_create_map_package"] =
                "The geospatial-mcp create_map_package schema admits extension members; Honua reads only the declared ones.",
            ["honua_create_app_package"] =
                "The geospatial-mcp create_app_package schema admits extension members; Honua reads only the declared ones.",
            ["honua_apply_style_preset"] =
                "The geospatial-mcp apply_style_preset schema admits extension members; Honua reads only the declared ones.",
        };

    [UnitTest]
    public void EveryTool_ClosesItsInputSchema_OrRecordsWhyItIsOpen()
    {
        var tools = BuildTools().ToDictionary(tool => tool.Name, StringComparer.Ordinal);

        var openWithoutReason = tools.Values
            .Where(tool => !IsClosed(tool.Describe().InputSchema))
            .Select(tool => tool.Name)
            .Where(name => !OpenInputSchemaReasons.ContainsKey(name))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var staleReasons = OpenInputSchemaReasons.Keys
            .Where(name => !tools.TryGetValue(name, out var tool) || IsClosed(tool.Describe().InputSchema))
            .Order(StringComparer.Ordinal)
            .ToArray();

        openWithoutReason.Should().BeEmpty(
            "a tool must set additionalProperties:false on its input schema, or record why it stays open "
            + "in OpenInputSchemaReasons");
        staleReasons.Should().BeEmpty("an open-schema reason must name an advertised tool that is still open");
    }

    [UnitTest]
    public void ToolsWithVendoredSchemas_AgreeWithThemOnClosedInputs()
    {
        var vendoredSchemas = VendoredInputSchemaPaths();
        var disagreements = new List<string>();
        foreach (var tool in BuildTools())
        {
            if (!vendoredSchemas.TryGetValue(tool.Name, out var standardPath))
            {
                continue;
            }

            var standardName = Path.GetFileName(standardPath);
            using var standard = JsonDocument.Parse(File.ReadAllText(standardPath));
            var liveClosed = IsClosed(tool.Describe().InputSchema);
            var standardClosed = IsClosed(standard.RootElement);
            if (liveClosed != standardClosed)
            {
                disagreements.Add($"{tool.Name} (live closed={liveClosed}) vs {standardName} (standard closed={standardClosed})");
            }

            // A closed live tool must still accept every member a standard-valid call may send.
            var undeclaredStandardMembers = PropertyNames(standard.RootElement)
                .Except(PropertyNames(tool.Describe().InputSchema), StringComparer.Ordinal)
                .ToArray();
            if (liveClosed && undeclaredStandardMembers.Length > 0)
            {
                disagreements.Add($"{tool.Name} refuses standard members: {string.Join(", ", undeclaredStandardMembers)}");
            }
        }

        disagreements.Should().BeEmpty(
            "a live tool and its vendored geospatial-mcp schema must agree on whether unknown arguments are refused");
    }

    [UnitTest]
    public void ClosedHandWrittenTool_RefusesAMisspelledArgument_BeforeItRuns()
    {
        var listLayers = BuildTools().Single(tool => tool.Name == "honua_list_layers");
        using var arguments = JsonDocument.Parse("""{ "fliter": "parcels" }""");

        var act = () => McpDeclaredArgumentGuard.EnsureDeclared(listLayers, arguments.RootElement);

        act.Should().Throw<GeoprocessingValidationException>()
            .WithMessage("*honua_list_layers*fliter*");
    }

    // Mapped standard tools plus the Honua extensions the vendored index pairs with a live tool.
    private static Dictionary<string, string> VendoredInputSchemaPaths()
    {
        var paths = ImplementedToolStandardNames.ToDictionary(
            pair => pair.Key,
            pair => Path.Join(SchemaRoot, "tools", pair.Value + ".schema.json"),
            StringComparer.Ordinal);
        using var index = JsonDocument.Parse(File.ReadAllText(Path.Join(SchemaRoot, "index.json")));
        foreach (var entry in index.RootElement.GetProperty("tools").EnumerateArray())
        {
            if (entry.TryGetProperty("referenceToolName", out var toolName)
                && toolName.GetString() is { Length: > 0 } name
                && entry.TryGetProperty("schema", out var schema)
                && Path.Join(SchemaRoot, schema.GetString()) is var path
                && File.Exists(path))
            {
                // The index also lists extension schemas this snapshot does not vendor.
                paths.TryAdd(name, path);
            }
        }

        return paths;
    }

    private static IEnumerable<string> PropertyNames(JsonElement schema) =>
        schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
            ? properties.EnumerateObject().Select(property => property.Name)
            : [];

    private static bool IsClosed(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object
        && schema.TryGetProperty("additionalProperties", out var additional)
        && additional.ValueKind == JsonValueKind.False;
}

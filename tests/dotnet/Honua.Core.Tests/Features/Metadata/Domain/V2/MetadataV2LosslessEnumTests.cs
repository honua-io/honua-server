// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

public sealed class MetadataV2LosslessEnumTests
{
    public static TheoryData<string, string> UnknownTokens()
    {
        var cases = new TheoryData<string, string>();
        string[] paths =
        [
            "resources/0/type", "resources/0/status/lifecycle", "resources/0/status/state",
            "resources/0/schemaFields/0/type", "resources/0/spatial/geometryType",
            "resources/0/attributeRules/0/type", "resources/0/symbology3D/rules/0/comparison",
            "connections/0/type", "storageBindings/0/storageType", "storageBindings/0/capabilities/0",
            "services/0/serviceType", "publications/0/publicationType"
        ];
        foreach (var path in paths)
        {
            foreach (var token in new[] { "\"future-value\"", "\"future-\\u0076alue\"", "\"\"", "987", "-987",
                "18446744073709551616", "-9223372036854775809" })
            {
                cases.Add(path, token);
            }
        }
        return cases;
    }

    [UnitTheory]
    [MemberData(nameof(UnknownTokens))]
    public void Serialize_UnrelatedEdit_RetainsUnknownEnumToken(string path, string token)
    {
        const string template = """
            {"resources":[{"metadata":{"id":"dataset"},"type":TOKEN,
              "status":{"lifecycle":TOKEN,"state":TOKEN},"schemaFields":[{"type":TOKEN}],
              "spatial":{"geometryType":TOKEN},"attributeRules":[{"type":TOKEN}],
              "symbology3D":{"rules":[{"attribute":"height","comparison":TOKEN}]}}],
             "connections":[{"type":TOKEN}],
             "storageBindings":[{"storageType":TOKEN,"capabilities":[TOKEN]}],
             "services":[{"serviceType":TOKEN}],"publications":[{"publicationType":TOKEN}]}
            """;
        var graph = JsonSerializer.Deserialize(template.Replace("TOKEN", token, StringComparison.Ordinal),
            MetadataV2JsonContext.Default.MetadataV2Graph)!;
        var edited = graph with
        {
            Resources = [graph.Resources[0] with { Metadata = graph.Resources[0].Metadata with { Title = "Edited" } }]
        };
        using var persisted = JsonDocument.Parse(JsonSerializer.Serialize(edited,
            MetadataV2JsonContext.Default.MetadataV2Graph));
        var value = persisted.RootElement;
        foreach (var segment in path.Split('/'))
        {
            value = value.ValueKind == JsonValueKind.Array ? value[int.Parse(segment,
                System.Globalization.CultureInfo.InvariantCulture)] : value.GetProperty(segment);
        }

        value.GetRawText().Should().Be(token);
    }

    [UnitTest]
    public void Serialize_ExplicitEnumEdit_ReplacesUnknownToken()
    {
        var graph = JsonSerializer.Deserialize("""{"resources":[{"type":"future-kind"}]}""",
            MetadataV2JsonContext.Default.MetadataV2Graph)!;
        var edited = graph with { Resources = [graph.Resources[0] with { Type = MetadataV2ResourceType.Table }] };
        var json = JsonSerializer.Serialize(edited, MetadataV2JsonContext.Default.MetadataV2Graph);

        JsonNode.Parse(json)!["resources"]![0]!["type"]!.GetValue<string>().Should().Be("table");
    }
}

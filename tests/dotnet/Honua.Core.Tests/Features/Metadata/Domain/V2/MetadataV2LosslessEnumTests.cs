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
            cases.Add(path, "-1");
            if (path is not "resources/0/status/state" and not "resources/0/schemaFields/0/type")
            {
                cases.Add(path, "\"UNKNOWN\"");
                cases.Add(path, "\"un\\u006bnown\"");
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
    public void Equals_NormalizedUnknownToken_MatchesDictionaryKeyWithoutChangingEncoding()
    {
        var escaped = JsonSerializer.Deserialize("""{"resources":[{"type":"future-\u0076alue"}]}""",
            MetadataV2JsonContext.Default.MetadataV2Graph)!;
        var normalized = JsonSerializer.Deserialize("""{"resources":[{"type":"future-value"}]}""",
            MetadataV2JsonContext.Default.MetadataV2Graph)!;
        var index = new[] { escaped.Resources.Single().TypeValue }.ToDictionary(kind => kind);

        index.ContainsKey(normalized.Resources.Single().TypeValue).Should().BeTrue();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(escaped, MetadataV2JsonContext.Default.MetadataV2Graph));
        json.RootElement.GetProperty("resources")[0].GetProperty("type").GetRawText()
            .Should().Be("\"future-\\u0076alue\"");
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

    [UnitTest]
    public void Serialize_ExplicitUnknownEdit_ReplacesRawToken()
    {
        var graph = JsonSerializer.Deserialize("""{"resources":[{"type":"future-kind"}]}""",
            MetadataV2JsonContext.Default.MetadataV2Graph)!;
        var edited = graph with { Resources = [graph.Resources[0] with { Type = MetadataV2ResourceType.Unknown }] };
        using var persisted = JsonDocument.Parse(JsonSerializer.Serialize(edited, MetadataV2JsonContext.Default.MetadataV2Graph));

        persisted.RootElement.GetProperty("resources")[0].GetProperty("type").GetString().Should().Be("unknown");
    }

    [UnitTest]
    public void Serialize_ExplicitCapabilitiesEdit_ReplacesRawTokens()
    {
        var graph = JsonSerializer.Deserialize("""{"storageBindings":[{"capabilities":["future-capability"]}]}""",
            MetadataV2JsonContext.Default.MetadataV2Graph)!;
        var edited = graph with
        {
            StorageBindings = [graph.StorageBindings[0] with { Capabilities = [MetadataV2StorageBindingCapability.Query] }]
        };
        using var persisted = JsonDocument.Parse(JsonSerializer.Serialize(edited, MetadataV2JsonContext.Default.MetadataV2Graph));

        persisted.RootElement.GetProperty("storageBindings")[0].GetProperty("capabilities").GetRawText().Should().Be("[\"query\"]");
    }

    [UnitTheory]
    [InlineData("\"1\"", "table")]
    [InlineData("\"+1\"", "table")]
    [InlineData("1", "table")]
    public void Serialize_KnownDatasetKind_PreservesExistingCanonicalEncoding(string token, string expected)
    {
        var graph = JsonSerializer.Deserialize("{\"resources\":[{\"type\":" + token + "}]}",
            MetadataV2JsonContext.Default.MetadataV2Graph)!;

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(graph, MetadataV2JsonContext.Default.MetadataV2Graph));

        graph.Resources.Single().Type.Should().Be(MetadataV2ResourceType.Table);
        json.RootElement.GetProperty("resources")[0].GetProperty("type").GetString().Should().Be(expected);
    }

    [UnitTheory]
    [InlineData("0")]
    [InlineData("\"UNKNOWN\"")]
    public void Serialize_KnownUnknownFieldType_PreservesCanonicalEncoding(string token)
    {
        var field = JsonSerializer.Deserialize("{\"type\":" + token + "}",
            MetadataV2JsonContext.Default.MetadataV2Field)!;

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(field, MetadataV2JsonContext.Default.MetadataV2Field));

        field.Type.Should().Be(MetadataV2FieldType.Unknown);
        json.RootElement.GetProperty("type").GetString().Should().Be("unknown");
    }
}

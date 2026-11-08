// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Admin;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Admin;

public sealed class MetadataGraphCompensationRobustnessTests
{
    private const string EnumGraph = """
        {"resources":[{"metadata":{"id":"dataset","title":"TITLE"},"type":TOKEN,
          "status":{"lifecycle":TOKEN,"state":TOKEN},"schemaFields":[{"name":"field","type":TOKEN}],
          "spatial":{"geometryType":TOKEN},"attributeRules":[{"name":"rule","type":TOKEN}],
          "symbology3D":{"rules":[{"attribute":"field","comparison":TOKEN}]}}],
         "connections":[{"metadata":{"id":"connection"},"type":TOKEN,"status":{"lifecycle":TOKEN,"state":TOKEN}}],
         "storageBindings":[{"metadata":{"id":"binding"},"resourceId":"dataset","connectionId":"connection",
           "storageType":TOKEN,"capabilities":[TOKEN],"status":{"lifecycle":TOKEN,"state":TOKEN}}],
         "services":[{"metadata":{"id":"service"},"serviceType":TOKEN,"status":{"lifecycle":TOKEN,"state":TOKEN}}],
         "publications":[{"metadata":{"id":"layer"},"resourceId":"dataset","serviceId":"service",
           "storageBindingId":"binding","publicationType":TOKEN,"status":{"lifecycle":TOKEN,"state":TOKEN}}]}
        """;

    public static TheoryData<string[]> TokenScenarios => new()
    {
        new[] { "\"future-\\u0076alue\"", "\"future-\\u0076alue\"", "\"future-\\u0076alue\"", "\"future-\\u0076alue\"" },
        new[] { "\"\"", "\"\"", "\"\"", "\"\"" },
        new[] { "18446744073709551616", "18446744073709551616", "18446744073709551616", "18446744073709551616" },
        new[] { "\"future-original\"", "\"future-failed\"", "\"future-failed\"", "\"future-original\"" },
        new[] { "\"future-original\"", "\"future-failed\"", "\"future-concurrent\"", "\"future-concurrent\"" },
        new[] { "\"future-original\"", "\"active\"", "\"active\"", "\"future-original\"" },
        new[] { "\"active\"", "\"future-failed\"", "\"future-concurrent\"", "\"future-concurrent\"" },
        new[] { "\"future-original\"", "\"future-\\u0076alue\"", "\"future-value\"", "\"future-original\"" },
        new[] { "\"future-original\"", "\"future-value\"", "\"future-\\u0076alue\"", "\"future-original\"" }
    };

    [UnitTheory]
    [MemberData(nameof(TokenScenarios))]
    public void BuildRebasedCompensatingMetadataV2Graph_UnknownEnumValues_RetainsOriginalOrConcurrentTokens(string[] tokens)
    {
        var previous = ReadEnumGraph(tokens[0], "Original");
        var persisted = ReadEnumGraph(tokens[1], "Failed");
        var current = ReadEnumGraph(tokens[2], "Failed");

        var restored = PostgreSqlLayerPublishingService.BuildRebasedCompensatingMetadataV2Graph(
            current, previous, persisted, DateTimeOffset.UnixEpoch);

        restored.Resources.Single().Metadata.Title.Should().Be("Original");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(restored, MetadataV2JsonContext.Default.MetadataV2Graph));
        foreach (var path in EnumPaths())
        {
            AtPath(json.RootElement, path).GetRawText().Should().Be(tokens[3], path);
        }
    }

    [UnitTheory]
    [InlineData("metadata/contactPoint", "{\"name\":\"Failed\"}", "{\"name\":null}")]
    [InlineData("spatial/spatialReference", "{\"srid\":4326}", "{\"srid\":null}")]
    [InlineData("spatial/storageCrs", "{\"srid\":4326}", "{\"srid\":null}")]
    [InlineData("spatial/bbox", "{\"west\":1}", "{\"west\":0}")]
    [InlineData("spatial", "{\"primaryGeometryField\":\"failed\"}", "{\"primaryGeometryField\":null}")]
    [InlineData("accessPolicy", "{\"allowAnonymous\":true}", "{\"allowAnonymous\":false}")]
    [InlineData("temporal", "{\"startTimeField\":\"failed\"}", "{\"startTimeField\":null}")]
    [InlineData("temporal/extent", "{\"start\":\"2026-01-01T00:00:00Z\"}", "{\"start\":null}")]
    public void BuildRebasedCompensatingMetadataV2Graph_ConcurrentExtensionOnlyObject_RetainsProperty(
        string path, string introduced, string reverted)
    {
        var previous = ReadResource(new JsonObject { ["metadata"] = new JsonObject { ["id"] = "dataset" } });
        var failedResource = JsonSerializer.SerializeToNode(previous.Resources.Single(), MetadataV2JsonContext.Default.MetadataV2Resource)!.AsObject();
        SetObject(failedResource, path, JsonNode.Parse(introduced)!.AsObject());
        var persisted = ReadResource(failedResource);
        var concurrentResource = failedResource.DeepClone().AsObject();
        var concurrentObject = JsonNode.Parse(introduced)!.AsObject();
        concurrentObject["futureProperty"] = "Concurrent";
        SetObject(concurrentResource, path, concurrentObject);
        var current = ReadResource(concurrentResource);

        var restored = PostgreSqlLayerPublishingService.BuildRebasedCompensatingMetadataV2Graph(
            current, previous, persisted, DateTimeOffset.UnixEpoch);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(restored.Resources.Single(), MetadataV2JsonContext.Default.MetadataV2Resource));
        var retained = AtPath(json.RootElement, path);
        retained.GetProperty("futureProperty").GetString().Should().Be("Concurrent");
        using var expected = JsonDocument.Parse(reverted);
        foreach (var property in expected.RootElement.EnumerateObject())
        {
            JsonElement.DeepEquals(retained.GetProperty(property.Name), property.Value).Should().BeTrue(property.Name);
        }
    }

    private static MetadataV2Graph ReadEnumGraph(string token, string title)
        => JsonSerializer.Deserialize(EnumGraph.Replace("TOKEN", token, StringComparison.Ordinal)
            .Replace("TITLE", title, StringComparison.Ordinal), MetadataV2JsonContext.Default.MetadataV2Graph)!;

    private static MetadataV2Graph ReadResource(JsonObject resource)
        => new() { Resources = [JsonSerializer.Deserialize(resource.ToJsonString(), MetadataV2JsonContext.Default.MetadataV2Resource)!] };

    private static JsonElement AtPath(JsonElement root, string path)
    {
        foreach (var segment in path.Split('/'))
        {
            root = root.ValueKind == JsonValueKind.Array
                ? root[int.Parse(segment, System.Globalization.CultureInfo.InvariantCulture)]
                : root.GetProperty(segment);
        }
        return root;
    }

    private static void SetObject(JsonObject root, string path, JsonObject value)
    {
        var segments = path.Split('/');
        foreach (var segment in segments[..^1])
        {
            if (root[segment] is not JsonObject nested)
            {
                nested = new JsonObject();
                root[segment] = nested;
            }
            root = nested;
        }
        root[segments[^1]] = value;
    }

    private static IEnumerable<string> EnumPaths()
    {
        yield return "resources/0/type";
        yield return "resources/0/schemaFields/0/type";
        yield return "resources/0/spatial/geometryType";
        yield return "resources/0/attributeRules/0/type";
        yield return "resources/0/symbology3D/rules/0/comparison";
        yield return "connections/0/type";
        yield return "storageBindings/0/storageType";
        yield return "storageBindings/0/capabilities/0";
        yield return "services/0/serviceType";
        yield return "publications/0/publicationType";
        foreach (var collection in new[] { "resources", "connections", "storageBindings", "services", "publications" })
        {
            yield return collection + "/0/status/lifecycle";
            yield return collection + "/0/status/state";
        }
    }
}

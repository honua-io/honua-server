// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

public sealed class MetadataV2RoutingRobustnessTests
{
    [UnitTheory]
    [InlineData("shape", "\"future-field\"")]
    [InlineData("start", "\"future-field\"")]
    [InlineData("end", "\"future-field\"")]
    [InlineData("shape", "18446744073709551616")]
    [InlineData("start", "987")]
    [InlineData("end", "-9223372036854775809")]
    [InlineData("shape", "\"unknown\"")]
    [InlineData("start", "\"unknown\"")]
    public void IsRoutable_UnsupportedSemanticField_BlocksOnlyAffectedDataset(string fieldName, string token)
    {
        var graph = SemanticFieldGraph();
        var dataset = graph.Resources[0];
        var fieldNode = JsonSerializer.SerializeToNode(dataset.SchemaFields.Single(field => field.Name == fieldName),
            MetadataV2JsonContext.Default.MetadataV2Field)!;
        fieldNode["type"] = JsonNode.Parse(token);
        var unsupportedField = JsonSerializer.Deserialize(fieldNode.ToJsonString(), MetadataV2JsonContext.Default.MetadataV2Field)!;
        var unsupported = dataset with
        {
            Metadata = dataset.Metadata with { Id = "future-dataset" },
            SchemaFields = dataset.SchemaFields.Select(field => field.Name == fieldName ? unsupportedField : field).ToArray(),
            StorageBindingIds = ["future-binding"]
        };
        var binding = graph.StorageBindings[0] with
        {
            Metadata = new() { Id = "future-binding" }, ResourceId = "future-dataset", StorageLayerId = 18
        };
        var layer = graph.Publications[0] with
        {
            Metadata = new() { Id = "future-layer" }, ResourceId = "future-dataset", StorageBindingId = "future-binding"
        };
        graph = graph with
        {
            Resources = [dataset, unsupported], StorageBindings = [graph.StorageBindings[0], binding],
            Publications = [graph.Publications[0], layer]
        };
        var snapshot = new MetadataV2GraphSnapshot(graph, "\"test\"", DateTimeOffset.UnixEpoch);

        snapshot.IsRoutable(layer).Should().BeFalse();
        layer.IsRoutable(unsupported).Should().BeFalse();
        snapshot.IsRoutable(binding, unsupported).Should().BeFalse();
        snapshot.IsRoutable(graph.Publications[0]).Should().BeTrue();
        MetadataV2GraphValidator.Validate(graph).Errors.Should().BeEmpty();
    }

    [UnitTheory]
    [InlineData("shape", MetadataV2FieldType.String)]
    [InlineData("start", MetadataV2FieldType.Integer)]
    [InlineData("end", MetadataV2FieldType.Geometry)]
    public void Validate_KnownIncorrectSemanticField_RejectsGraph(string fieldName, MetadataV2FieldType fieldType)
    {
        var graph = SemanticFieldGraph();
        var dataset = graph.Resources[0];
        graph = graph with { Resources = [dataset with
        {
            SchemaFields = dataset.SchemaFields.Select(field => field.Name == fieldName ? field with { Type = fieldType } : field).ToArray()
        }] };

        MetadataV2GraphValidator.Validate(graph).IsValid.Should().BeFalse();
        new MetadataV2GraphSnapshot(graph, "\"test\"", DateTimeOffset.UnixEpoch)
            .IsRoutable(graph.Publications[0]).Should().BeFalse();
    }

    [UnitTheory]
    [InlineData("shape")]
    [InlineData("start")]
    [InlineData("end")]
    public void Validate_MissingSemanticField_RejectsGraph(string fieldName)
    {
        var graph = SemanticFieldGraph();
        var dataset = graph.Resources[0];
        graph = graph with { Resources = [dataset with { SchemaFields = dataset.SchemaFields.Where(field => field.Name != fieldName).ToArray() }] };

        MetadataV2GraphValidator.Validate(graph).IsValid.Should().BeFalse();
        new MetadataV2GraphSnapshot(graph, "\"test\"", DateTimeOffset.UnixEpoch)
            .IsRoutable(graph.Publications[0]).Should().BeFalse();
    }

    private static MetadataV2Graph SemanticFieldGraph()
    {
        var graph = JsonSerializer.Deserialize(KnownGraph, MetadataV2JsonContext.Default.MetadataV2Graph)!;
        return graph with { Resources = [graph.Resources[0] with
        {
            Spatial = new() { PrimaryGeometryField = "SHAPE" },
            Temporal = new() { StartTimeField = "START", EndTimeField = "END" },
            SchemaFields =
            [
                new() { Name = "shape", Type = MetadataV2FieldType.Geometry },
                new() { Name = "start", Type = MetadataV2FieldType.Date },
                new() { Name = "end", Type = MetadataV2FieldType.DateTime }
            ]
        }] };
    }

    private const string KnownGraph = """
        {"catalogs":[],"projectionProfiles":[],"policies":[],"roles":[],"services":[{"metadata":{"id":"service","name":"service"}}],
         "resources":[{"metadata":{"id":"dataset","name":"dataset"},"storageBindingIds":["binding"]}],
         "connections":[{"metadata":{"id":"connection"},"type":"managed"}],
         "storageBindings":[{"metadata":{"id":"binding"},"resourceId":"dataset","connectionId":"connection","storageLayerId":17}],
         "publications":[{"metadata":{"id":"layer"},"resourceId":"dataset","serviceId":"service"}]}
        """;

    [UnitTheory]
    [InlineData("services", "status", "{\"lifecycle\":\"future-status\"}")]
    [InlineData("resources", "status", "{\"lifecycle\":\"future-status\"}")]
    [InlineData("publications", "status", "{\"lifecycle\":\"future-status\"}")]
    [InlineData("storageBindings", "status", "{\"lifecycle\":\"future-status\"}")]
    [InlineData("services", "status", "{\"lifecycle\":987}")]
    [InlineData("resources", "status", "{\"lifecycle\":987}")]
    [InlineData("publications", "status", "{\"lifecycle\":987}")]
    [InlineData("storageBindings", "status", "{\"lifecycle\":987}")]
    [InlineData("services", "status", "{\"lifecycle\":-1}")]
    [InlineData("resources", "status", "{\"lifecycle\":-1}")]
    [InlineData("publications", "status", "{\"lifecycle\":-1}")]
    [InlineData("storageBindings", "status", "{\"lifecycle\":-1}")]
    [InlineData("connections", "status", "{\"lifecycle\":\"future-status\"}")]
    [InlineData("connections", "type", "\"future-kind\"")]
    [InlineData("services", "serviceType", "\"future-kind\"")]
    [InlineData("resources", "type", "\"future-kind\"")]
    [InlineData("publications", "publicationType", "\"future-kind\"")]
    [InlineData("storageBindings", "storageType", "\"future-kind\"")]
    public void IsRoutable_UnsupportedPersistedValue_DoesNotServe(string collection, string property, string json)
    {
        var node = JsonNode.Parse(KnownGraph)!;
        node[collection]![0]![property] = JsonNode.Parse(json);
        var graph = JsonSerializer.Deserialize(node.ToJsonString(), MetadataV2JsonContext.Default.MetadataV2Graph)!;
        var snapshot = new MetadataV2GraphSnapshot(graph, "\"test\"", DateTimeOffset.UnixEpoch);

        snapshot.IsRoutable(graph.Publications.Single()).Should().BeFalse();
    }

    [UnitTest]
    public void IsRoutable_OmittedStatuses_PreservesServingDefaults()
    {
        var graph = JsonSerializer.Deserialize(KnownGraph, MetadataV2JsonContext.Default.MetadataV2Graph)!;
        var snapshot = new MetadataV2GraphSnapshot(graph, "\"test\"", DateTimeOffset.UnixEpoch);

        snapshot.IsRoutable(graph.Publications.Single()).Should().BeTrue();
    }

    [UnitTheory]
    [InlineData("{}")]
    [InlineData("{\"lifecycle\":null,\"state\":null}")]
    [InlineData("null")]
    public void Deserialize_OmittedOrNullLifecycle_PreservesActiveDefault(string json)
    {
        var status = JsonSerializer.Deserialize(json, MetadataV2JsonContext.Default.MetadataV2Status)!;

        status.Lifecycle.Should().Be(MetadataV2LifecycleStatus.Active);
        status.State.Should().Be(MetadataV2OperationalState.Unknown);
    }

    [UnitTheory]
    [InlineData("{\"lifecycle\":\"future-status\",\"state\":\"future-state\"}")]
    [InlineData("{\"lifecycle\":987,\"state\":987}")]
    [InlineData("{\"lifecycle\":-1,\"state\":-1}")]
    [InlineData("{\"lifecycle\":2147483648,\"state\":9223372036854775808}")]
    [InlineData("{\"lifecycle\":18446744073709551616,\"state\":-9223372036854775809}")]
    [InlineData("{\"lifecycle\":-9223372036854775809,\"state\":18446744073709551616}")]
    [InlineData("{\"lifecycle\":\"1\",\"state\":\"3\"}")]
    public void Deserialize_UnsupportedStatus_PreservesExplicitUnknown(string json)
    {
        var status = JsonSerializer.Deserialize(json, MetadataV2JsonContext.Default.MetadataV2Status)!;

        status.Lifecycle.ToString().Should().Be("Unknown");
        status.State.Should().Be(MetadataV2OperationalState.Unknown);
    }

    [UnitTheory]
    [InlineData(0, "draft", "unknown")]
    [InlineData(1, "active", "ready")]
    [InlineData(2, "deprecated", "pending")]
    [InlineData(3, "retired", "degraded")]
    [InlineData(4, "archived", "failed")]
    public void Serialize_KnownNumericStatus_PreservesWireNames(int code, string lifecycle, string state)
    {
        var json = $"{{\"lifecycle\":{code},\"state\":{code}}}";
        var status = JsonSerializer.Deserialize(json, MetadataV2JsonContext.Default.MetadataV2Status)!;
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(status,
            MetadataV2JsonContext.Default.MetadataV2Status));

        ((int)status.Lifecycle).Should().Be(code);
        ((int)status.State).Should().Be(code);
        serialized.RootElement.GetProperty("lifecycle").GetString().Should().Be(lifecycle);
        serialized.RootElement.GetProperty("state").GetString().Should().Be(state);
    }

    [UnitTheory]
    [InlineData("{\"lifecycle\":true}")]
    [InlineData("{\"lifecycle\":{}}")]
    [InlineData("{\"lifecycle\":[]}")]
    [InlineData("{\"lifecycle\":1.5}")]
    [InlineData("{\"state\":false}")]
    [InlineData("{\"state\":{}}")]
    [InlineData("{\"state\":[]}")]
    [InlineData("{\"state\":1.5}")]
    [InlineData("true")]
    [InlineData("[]")]
    public void Deserialize_MalformedStatusToken_RejectsValue(string json)
    {
        var deserialize = () => JsonSerializer.Deserialize(json, MetadataV2JsonContext.Default.MetadataV2Status);

        deserialize.Should().Throw<JsonException>();
    }

    [UnitTheory]
    [InlineData("services")]
    [InlineData("resources")]
    [InlineData("publications")]
    [InlineData("storageBindings")]
    [InlineData("connections")]
    public void IsRoutable_UndefinedInMemoryKind_DoesNotServe(string collection)
    {
        const int undefinedKind = 987;
        var original = JsonSerializer.Deserialize(KnownGraph, MetadataV2JsonContext.Default.MetadataV2Graph)!;
        var graph = collection switch
        {
            "services" => original with
            {
                Services = [original.Services.Single() with { ServiceType = (MetadataV2ServiceType)undefinedKind }]
            },
            "resources" => original with
            {
                Resources = [original.Resources.Single() with { Type = (MetadataV2ResourceType)undefinedKind }]
            },
            "publications" => original with
            {
                Publications = [original.Publications.Single() with { PublicationType = (MetadataV2PublicationType)undefinedKind }]
            },
            "storageBindings" => original with
            {
                StorageBindings = [original.StorageBindings.Single() with { StorageType = (MetadataV2StorageType)undefinedKind }]
            },
            "connections" => original with
            {
                Connections = [original.Connections.Single() with { Type = (MetadataV2ConnectionType)undefinedKind }]
            },
            _ => throw new ArgumentOutOfRangeException(nameof(collection))
        };
        var snapshot = new MetadataV2GraphSnapshot(graph, "\"test\"", DateTimeOffset.UnixEpoch);

        snapshot.IsRoutable(graph.Publications.Single()).Should().BeFalse();
    }
}

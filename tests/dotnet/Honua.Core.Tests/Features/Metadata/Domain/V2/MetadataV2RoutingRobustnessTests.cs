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
                Resources = [original.Resources.Single() with { Type = (MetadataV2ObjectType)undefinedKind }]
            },
            "publications" => original with
            {
                Publications = [original.Publications.Single() with { PublicationType = (MetadataV2LayerType)undefinedKind }]
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

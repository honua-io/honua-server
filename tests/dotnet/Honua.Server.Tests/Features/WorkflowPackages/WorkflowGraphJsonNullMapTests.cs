// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Server.Features.WorkflowPackages;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.WorkflowPackages;

/// <summary>
/// A saved graph that omits (or nulls) its optional maps must read back with empty maps, never
/// <c>null</c>: the domain types declare them non-nullable. Source-generated System.Text.Json
/// assigns <c>default</c> to omitted init-only members of records with required members, which
/// previously overwrote the empty-map initializers.
/// </summary>
[Protocol(TestProtocols.Admin)]
[Operation(Operations.ProcessExecution)]
public sealed class WorkflowGraphJsonNullMapTests
{
    [UnitTheory]
    [InlineData("""{"schemaVersion":"workflow-package.v1","nodes":[{"nodeId":"sink","nodeTypeId":"process:sink.external-postgis","parameters":{"input":"x"}}],"edges":[]}""")]
    [InlineData("""{"nodes":[{"nodeId":"sink","nodeTypeId":"process:sink.external-postgis","parameters":null,"metadata":null}],"edges":null,"editorMetadata":null}""")]
    public void OmittedOrNullGraphMaps_ReadBackAsEmpty(string json)
    {
        var graph = JsonSerializer.Deserialize(json, WorkflowPackagesJsonContext.Default.WorkflowGraph)!;

        graph.EditorMetadata.Should().NotBeNull().And.BeEmpty();
        graph.Edges.Should().NotBeNull().And.BeEmpty();
        graph.Nodes.Should().ContainSingle();
        graph.Nodes[0].Metadata.Should().NotBeNull().And.BeEmpty();
        graph.Nodes[0].Parameters.Should().NotBeNull();

        var written = JsonSerializer.Serialize(graph, WorkflowPackagesJsonContext.Default.WorkflowGraph);
        using var document = JsonDocument.Parse(written);
        document.RootElement.GetProperty("editorMetadata").ValueKind.Should().Be(JsonValueKind.Object);
        document.RootElement.GetProperty("nodes")[0].GetProperty("metadata").ValueKind.Should().Be(JsonValueKind.Object);
    }

    [UnitTest]
    public void AuthoredGraphMaps_AreKept()
    {
        const string json = """{"nodes":[{"nodeId":"a","nodeTypeId":"t","parameters":{"p":"1"},"metadata":{"m":"2"}}],"editorMetadata":{"layout":"lr"}}""";

        var graph = JsonSerializer.Deserialize(json, WorkflowPackagesJsonContext.Default.WorkflowGraph)!;

        graph.EditorMetadata.Should().Equal(new Dictionary<string, string> { ["layout"] = "lr" });
        graph.Nodes[0].Parameters.Should().Equal(new Dictionary<string, string> { ["p"] = "1" });
        graph.Nodes[0].Metadata.Should().Equal(new Dictionary<string, string> { ["m"] = "2" });
    }
}

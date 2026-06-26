// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing.Cli.Publish;
using Xunit;

namespace Honua.Geoprocessing.Cli.Tests;

public sealed class GpWorkflowSourceTests : IDisposable
{
    private readonly string _fixtureRoot = Path.Combine(
        Path.GetTempPath(),
        "gp-publish-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_fixtureRoot))
        {
            Directory.Delete(_fixtureRoot, recursive: true);
        }
    }

    private sealed class StubCatalog(params ProcessDefinition[] processes) : IProcessCatalog
    {
        private readonly Dictionary<string, ProcessDefinition> _byId =
            processes.ToDictionary(p => p.ProcessId, StringComparer.Ordinal);

        public ProcessDefinition? GetProcess(string processId)
            => _byId.GetValueOrDefault(processId);

        public IReadOnlyList<ProcessDefinition> ListProcesses() => processes;

        public IReadOnlyList<ProcessDefinition> GetProcessesByCategory(string category)
            => processes.Where(p => p.Category == category).ToArray();
    }

    private static ProcessDefinition BufferProcess()
        => new()
        {
            ProcessId = "geometry.buffer",
            Title = "Buffer",
            Description = "Buffer a geometry",
            Category = "geometry",
            Parameters =
            [
                new ProcessParameterSpec
                {
                    Name = "distance",
                    DisplayName = "Distance",
                    Description = "Buffer distance",
                    ValueType = ProcessParameterValueType.FloatingPoint,
                    Required = true,
                    DefaultValue = "10"
                },
                new ProcessParameterSpec
                {
                    Name = "wkb",
                    DisplayName = "Geometry",
                    Description = "Input geometry",
                    ValueType = ProcessParameterValueType.Wkb,
                    Required = true
                }
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer],
            RuntimeProfile = "managed"
        };

    private GpWorkflowSource CreateSource(params string[] registeredIds)
        => new(new StubCatalog(BufferProcess()), registeredIds.ToHashSet(StringComparer.Ordinal));

    [Fact]
    public void Resolve_CodeProcessWithoutWrapFlag_IsNotPublishable_AndExplainsImageDeploy()
    {
        var source = CreateSource("geometry.buffer");

        var result = source.Resolve("geometry.buffer", filePath: null, asProcessNode: false, name: null, _fixtureRoot);

        Assert.False(result.CanPublish);
        Assert.Equal("code process", result.Kind);
        Assert.Equal("geometry.buffer", result.ProcessId);
        Assert.Contains("server image", result.Reason, StringComparison.Ordinal);
        Assert.Contains("--as-process-node", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_CodeProcessWithWrapFlag_BuildsSingleProcessNodeGraph()
    {
        var source = CreateSource("geometry.buffer");

        var result = source.Resolve("geometry.buffer", filePath: null, asProcessNode: true, name: null, _fixtureRoot);

        Assert.True(result.CanPublish);
        Assert.Equal("workflow (process node)", result.Kind);
        var node = Assert.Single(result.Graph!.Nodes);
        Assert.Equal("process:geometry.buffer", node.NodeTypeId);
        // Default-valued parameters are seeded; required-without-default are not.
        Assert.Equal("10", node.Parameters["distance"]);
        Assert.False(node.Parameters.ContainsKey("wkb"));
    }

    [Fact]
    public void Resolve_WorkflowFixture_IsPublishable()
    {
        Directory.CreateDirectory(Path.Combine(_fixtureRoot, "my-flow"));
        File.WriteAllText(
            Path.Combine(_fixtureRoot, "my-flow", "workflow.json"),
            """{"schemaVersion":"workflow-package.v1","nodes":[{"nodeId":"n1","nodeTypeId":"process:geometry.buffer"}]}""");

        var source = CreateSource("geometry.buffer");

        var result = source.Resolve("my-flow", filePath: null, asProcessNode: false, name: null, _fixtureRoot);

        Assert.True(result.CanPublish);
        Assert.Equal("workflow (fixture)", result.Kind);
        Assert.Single(result.Graph!.Nodes);
    }

    [Fact]
    public void Resolve_ExplicitFile_OverridesFixtureAndProcess()
    {
        var path = Path.Combine(_fixtureRoot, "explicit.json");
        Directory.CreateDirectory(_fixtureRoot);
        File.WriteAllText(
            path,
            """{"schemaVersion":"workflow-package.v1","nodes":[{"nodeId":"a","nodeTypeId":"process:geometry.area"},{"nodeId":"b","nodeTypeId":"process:geometry.buffer"}]}""");

        var source = CreateSource("geometry.buffer");

        var result = source.Resolve("geometry.buffer", filePath: path, asProcessNode: false, name: "named", _fixtureRoot);

        Assert.True(result.CanPublish);
        Assert.Equal("workflow (file)", result.Kind);
        Assert.Equal("named", result.Name);
        Assert.Equal(2, result.Graph!.Nodes.Count);
    }

    [Fact]
    public void Resolve_UnknownId_IsNotPublishable_AndIsAnError()
    {
        var source = CreateSource("geometry.buffer");

        var result = source.Resolve("does-not-exist", filePath: null, asProcessNode: false, name: null, _fixtureRoot);

        Assert.False(result.CanPublish);
        Assert.Equal("unknown", result.Kind);
        Assert.Contains("does-not-exist", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_MissingFile_ThrowsUsageException()
    {
        var source = CreateSource("geometry.buffer");

        Assert.Throws<GpCliUsageException>(() =>
            source.Resolve("x", filePath: Path.Combine(_fixtureRoot, "nope.json"), asProcessNode: false, name: null, _fixtureRoot));
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Geoprocessing.Cli.Publish;

internal sealed record GpPublishSource
{
    public bool CanPublish { get; init; }

    public WorkflowGraph? Graph { get; init; }

    public string? Name { get; init; }

    public required string Kind { get; init; }

    public string? Reason { get; init; }

    public string? ProcessId { get; init; }
}

internal sealed class GpWorkflowSource(IProcessCatalog? catalog, IReadOnlySet<string> registeredProcessIds)
{
    private static readonly JsonSerializerOptions GraphReadOptions = GpPublishJson.Options;

    public GpPublishSource Resolve(
        string id,
        string? filePath,
        bool asProcessNode,
        string? name,
        string fixtureRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        // 1. Explicit graph file wins: this is a hand-authored or exported workflow artifact.
        if (filePath is not null)
        {
            var graph = ReadGraphFile(filePath);
            return new GpPublishSource
            {
                CanPublish = true,
                Graph = graph,
                Name = name ?? id,
                Kind = "workflow (file)"
            };
        }

        // 2. Local workflow fixture: tests/fixtures/gp/<id>/workflow.json. Guard against a rooted
        //    id: Path.Combine silently drops fixtureRoot when a later segment is absolute,
        //    which would let an absolute id escape the fixture root entirely. An id is a
        //    workflow/process identifier, never a path, so a rooted id just falls through
        //    to the code-process/unknown-id handling below instead of resolving a fixture.
        if (!Path.IsPathRooted(id))
        {
            var fixturePath = Path.Join(fixtureRoot, id, "workflow.json");
            if (File.Exists(fixturePath))
            {
                var graph = ReadGraphFile(fixturePath);
                return new GpPublishSource
                {
                    CanPublish = true,
                    Graph = graph,
                    Name = name ?? id,
                    Kind = "workflow (fixture)"
                };
            }
        }

        // 3. A registered code process. By default this is NOT publishable as a package;
        //    it deploys via the server image. With --as-process-node we wrap it as a
        //    one-node graph the same way the Console drops a single process onto the canvas.
        if (registeredProcessIds.Contains(id))
        {
            if (!asProcessNode)
            {
                return new GpPublishSource
                {
                    CanPublish = false,
                    Kind = "code process",
                    ProcessId = id,
                    Reason =
                        $"'{id}' is a built-in geoprocessing process: it is CODE that ships with the "
                        + "server image, not a data workflow, so it deploys by releasing a new server "
                        + "image rather than through the WorkflowPackage store. To publish it as a "
                        + "single-node workflow package (a process-endpoint wrapper, like dropping it "
                        + "onto the Console canvas), re-run with --as-process-node. To publish an "
                        + "authored multi-step workflow, add tests/fixtures/gp/" + id + "/workflow.json or "
                        + "pass --file <workflow.json>."
                };
            }

            var graph = BuildSingleProcessGraph(id);
            return new GpPublishSource
            {
                CanPublish = true,
                Graph = graph,
                Name = name ?? id,
                Kind = "workflow (process node)",
                ProcessId = id
            };
        }

        // 4. Unknown id with no fixture and no file.
        return new GpPublishSource
        {
            CanPublish = false,
            Kind = "unknown",
            Reason =
                $"No publishable workflow found for '{id}'. Expected a fixture at "
                + $"tests/fixtures/gp/{id}/workflow.json, an explicit --file <workflow.json>, or a registered "
                + "process id used with --as-process-node. Run 'honua gp list' to see registered processes."
        };
    }

    internal WorkflowGraph BuildSingleProcessGraph(string processId)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        var definition = catalog?.GetProcess(processId);
        if (definition is not null)
        {
            // Seed default-valued parameters so the published node carries the process's declared
            // defaults; required-without-default parameters are left for the run-time caller to bind.
            foreach (var parameter in definition.Parameters.Where(parameter => parameter.DefaultValue is not null))
            {
                parameters[parameter.Name] = parameter.DefaultValue!;
            }
        }

        var node = new WorkflowNode
        {
            NodeId = "n1",
            NodeTypeId = ProcessNodeTypePrefix + processId,
            Parameters = parameters,
            WorkerProfile = ResolveWorkerProfile(definition)
        };

        return new WorkflowGraph
        {
            SchemaVersion = "workflow-package.v1",
            Nodes = [node],
            Edges = [],
            WorkerProfile = ResolveWorkerProfile(definition)
        };
    }

    internal const string ProcessNodeTypePrefix = "process:";

    private static string ResolveWorkerProfile(ProcessDefinition? definition)
        => definition is { RuntimeProfile.Length: > 0 } ? definition.RuntimeProfile : "managed";

    private static WorkflowGraph ReadGraphFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new GpCliUsageException($"Workflow graph file not found: {path}");
        }

        WorkflowGraph? graph;
        try
        {
            var json = File.ReadAllText(path);
            graph = JsonSerializer.Deserialize<WorkflowGraph>(json, GraphReadOptions);
        }
        catch (JsonException ex)
        {
            throw new GpCliUsageException($"Failed to parse workflow graph '{path}': {ex.Message}");
        }

        if (graph is null || graph.Nodes is null)
        {
            throw new GpCliUsageException(
                $"Workflow graph '{path}' is empty or missing a 'nodes' array.");
        }

        return graph;
    }
}

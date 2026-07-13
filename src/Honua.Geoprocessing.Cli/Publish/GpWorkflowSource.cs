// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Geoprocessing.Cli.Publish;

/// <summary>
/// Outcome of resolving a <c>honua gp publish &lt;id&gt;</c> identifier into a publishable
/// <see cref="WorkflowGraph"/>, including the publish/skip semantics for the id kind.
/// </summary>
internal sealed record GpPublishSource
{
    /// <summary>Whether a publishable graph was produced.</summary>
    public bool CanPublish { get; init; }

    /// <summary>The graph to push to the package store, when <see cref="CanPublish"/> is true.</summary>
    public WorkflowGraph? Graph { get; init; }

    /// <summary>Human-readable package name.</summary>
    public string? Name { get; init; }

    /// <summary>How the id resolved (authored workflow, fixture, or wrapped code process).</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// When <see cref="CanPublish"/> is false, the honest reason the id does not map to a
    /// publishable data artifact (e.g. a built-in code process that deploys via the image).
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>The process id when the source wrapped a single registered process.</summary>
    public string? ProcessId { get; init; }
}

/// <summary>
/// Resolves a <c>honua gp publish</c> identifier into a <see cref="WorkflowGraph"/>, enforcing
/// the process-vs-workflow publish semantics.
/// </summary>
/// <remarks>
/// <para>
/// Honua geoprocessing <b>processes</b> (e.g. <c>geometry.buffer</c>) are CODE: they are
/// registered <c>IProcessExecutor</c>s compiled into the server image and discovered through
/// the <see cref="IProcessCatalog"/>. They are not data artifacts and therefore do not, on
/// their own, deploy through the versioned WorkflowPackage store — a code process ships with
/// the server image and is promoted by rebuilding/releasing that image, not by publishing a
/// package. So <c>gp publish &lt;processId&gt;</c> is, by default, a no-op that prints that
/// guidance.
/// </para>
/// <para>
/// Authored <b>workflows</b> (a <see cref="WorkflowGraph"/> of nodes/edges, exactly what the
/// Console map-builder produces) ARE the data artifact the package store versions and the
/// GitOps cross-env release path promotes. A workflow is resolved from a local fixture at
/// <c>samples/gp/&lt;id&gt;/workflow.json</c> or from an explicit <c>--file</c>.
/// </para>
/// <para>
/// As a convenience that mirrors dragging a single process onto the Console canvas, a single
/// registered process can be wrapped into a one-node graph with <c>--as-process-node</c>, which
/// makes the code process publishable as a package targeting a process endpoint.
/// </para>
/// </remarks>
internal sealed class GpWorkflowSource(IProcessCatalog? catalog, IReadOnlySet<string> registeredProcessIds)
{
    private static readonly JsonSerializerOptions GraphReadOptions = GpPublishJson.Options;

    /// <summary>
    /// Resolves <paramref name="id"/> to a publishable graph or an honest skip reason.
    /// </summary>
    /// <param name="id">A workflow id or a registered process id.</param>
    /// <param name="filePath">An explicit graph JSON file, overriding fixture/process resolution.</param>
    /// <param name="asProcessNode">Wrap a single registered process into a one-node graph.</param>
    /// <param name="name">An explicit package name; defaults to the id.</param>
    /// <param name="fixtureRoot">Root directory holding <c>&lt;id&gt;/workflow.json</c> fixtures.</param>
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

        // 2. Local workflow fixture: samples/gp/<id>/workflow.json. Guard against a rooted
        //    id: Path.Combine silently drops fixtureRoot when a later segment is absolute,
        //    which would let an absolute id escape the fixture root entirely. An id is a
        //    workflow/process identifier, never a path, so a rooted id just falls through
        //    to the code-process/unknown-id handling below instead of resolving a fixture.
        if (!Path.IsPathRooted(id))
        {
            var fixturePath = Path.Combine(fixtureRoot, id, "workflow.json");
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
                        + "authored multi-step workflow, add samples/gp/" + id + "/workflow.json or "
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
                + $"samples/gp/{id}/workflow.json, an explicit --file <workflow.json>, or a registered "
                + "process id used with --as-process-node. Run 'honua gp list' to see registered processes."
        };
    }

    /// <summary>
    /// Wraps a single registered process into a one-node <see cref="WorkflowGraph"/> whose node
    /// type is <c>process:&lt;id&gt;</c> — the same node-type convention the server's workflow
    /// node registry uses for geoprocessing processes.
    /// </summary>
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

    /// <summary>
    /// Node-type prefix for geoprocessing process nodes. Kept in sync with the server's
    /// <c>ProcessCatalogWorkflowNodeProvider.NodeTypePrefix</c>.
    /// </summary>
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

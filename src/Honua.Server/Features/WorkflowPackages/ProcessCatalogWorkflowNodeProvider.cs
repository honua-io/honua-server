// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Geoprocessing;

namespace Honua.Server.Features.WorkflowPackages;

internal sealed class ProcessCatalogWorkflowNodeProvider(IProcessCatalog processCatalog) : IWorkflowNodeProvider
{
    public const string Provider = "geoprocessing.process-catalog";
    public const string NodeTypePrefix = "process:";

    public string ProviderId => Provider;

    public string Version => BuiltInProcessCatalog.CatalogVersion;

    public Task<IReadOnlyList<WorkflowNodeDefinition>> ListNodesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Advertise a process as a graph node ONLY on the workflow entry point it declares.
        // Protocol-only operations reach their owning synchronous endpoint and have no
        // dispatcher executor, so offering them to a graph author advertises a node that
        // can never run (#4409).
        var nodes = processCatalog
            .ListProcesses()
            .Where(ProcessExecutionCapabilityCatalog.IsWorkflowComposable)
            .OrderBy(process => process.Category, StringComparer.Ordinal)
            .ThenBy(process => process.ProcessId, StringComparer.Ordinal)
            .Select(ToNodeDefinition)
            .ToArray();

        return Task.FromResult<IReadOnlyList<WorkflowNodeDefinition>>(nodes);
    }

    public Task<IReadOnlyList<string>> GetWarningsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    public static string ToNodeTypeId(string processId) => NodeTypePrefix + processId;

    public static bool TryGetProcessId(string nodeTypeId, out string processId)
    {
        if (nodeTypeId.StartsWith(NodeTypePrefix, StringComparison.Ordinal))
        {
            processId = nodeTypeId[NodeTypePrefix.Length..];
            return !string.IsNullOrWhiteSpace(processId);
        }

        processId = string.Empty;
        return false;
    }

    private static WorkflowNodeDefinition ToNodeDefinition(ProcessDefinition process)
    {
        var jobCallable = ProcessExecutionEligibility.IsJobCallable(process);
        var workflowCallable = ProcessExecutionEligibility.IsWorkflowCallable(process);
        var executable = jobCallable || workflowCallable;

        var parameterSchemas = process.Parameters
            .Select(parameter => new WorkflowNodeParameterSchema
            {
                Name = parameter.Name,
                DisplayName = parameter.DisplayName,
                Description = parameter.Description,
                Required = parameter.Required,
                DefaultValue = parameter.DefaultValue,
                Schema = WorkflowSchemaMappings.FromProcessParameterType(parameter.ValueType)
            })
            .ToArray();

        var inputSchemas = process.Parameters
            .Where(parameter => parameter.Required)
            .Select(parameter => new WorkflowNodePortSchema
            {
                Name = parameter.Name,
                Title = parameter.DisplayName,
                Required = true,
                Schema = WorkflowSchemaMappings.FromProcessParameterType(parameter.ValueType)
            })
            .ToArray();

        var outputSchemas = process.OutputArtifactKinds
            .Select(kind => new WorkflowNodePortSchema
            {
                Name = kind.ToString(),
                Title = kind.ToString(),
                Required = false,
                Schema = WorkflowSchemaMappings.FromArtifactKind(kind)
            })
            .ToArray();

        return new WorkflowNodeDefinition
        {
            NodeTypeId = ToNodeTypeId(process.ProcessId),
            ProviderId = Provider,
            RuntimeKind = WorkflowNodeRuntimeKind.Geoprocessing,
            Title = process.Title,
            Description = process.Description,
            Category = process.Category,
            ParameterSchemas = parameterSchemas,
            InputSchemas = inputSchemas,
            OutputSchemas = outputSchemas,
            CapabilityFlags = new WorkflowNodeCapabilityFlags
            {
                CanValidate = true,
                CanDryRun = true,
                SupportsJob = jobCallable,
                SupportsSchedule = executable,
                SupportsProcessEndpoint = jobCallable,
                Executable = executable
            },
            RuntimeHints = new WorkflowNodeRuntimeHints
            {
                WorkerProfile = "geoprocessing",
                EstimatedDurationSeconds = Math.Max(1, process.Parameters.Count),
                CostWeight = Math.Max(1, process.Parameters.Count / 2.0),
                CostUnit = "relative-cpu"
            },
            ProcessId = process.ProcessId
        };
    }
}

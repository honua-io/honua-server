// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Server.Features.Geoprocessing;

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

        var nodes = processCatalog
            .ListProcesses()
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
                SupportsJob = true,
                SupportsSchedule = true,
                SupportsProcessEndpoint = true,
                Executable = true
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

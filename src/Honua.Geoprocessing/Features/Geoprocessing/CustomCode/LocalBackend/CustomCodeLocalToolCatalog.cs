// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Geoprocessing.CustomCode.LocalBackend;

/// <summary>
/// Decorates the built-in <see cref="IProcessCatalog"/> so operator-registered local custom tools are
/// discoverable through the GPServer task-listing / task-info routes (and every other catalog reader).
/// Closes the "a registered custom tool is not discoverable" gap without touching the frozen built-in
/// catalog: the built-in processes are surfaced unchanged, and each configured
/// <see cref="CustomCodeLocalToolDefinition"/> is projected into a <see cref="ProcessDefinition"/>
/// carrying the <c>custom-code</c> runtime profile and its declared input parameter schema.
/// </summary>
/// <remarks>
/// This adds discovery metadata only. A listed tool does not by itself grant execution — running it is
/// still gated by the repo allowlist, the submit-time validation, and the local backend's controls.
/// Only registered when at least one tool is configured, so a deployment with no tools sees the exact
/// built-in catalog it saw before (behavior-preserving).
/// </remarks>
internal sealed class CustomCodeLocalToolCatalog : IProcessCatalog
{
    private readonly BuiltInProcessCatalog _inner;
    private readonly ImmutableDictionary<string, ProcessDefinition> _tools;

    public CustomCodeLocalToolCatalog(
        BuiltInProcessCatalog inner,
        IOptions<CustomCodeLocalBackendOptions> options)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(options);
        _inner = inner;

        var builder = ImmutableDictionary.CreateBuilder<string, ProcessDefinition>(StringComparer.Ordinal);
        foreach (var tool in options.Value.Tools)
        {
            if (string.IsNullOrWhiteSpace(tool.ProcessId) || inner.GetProcess(tool.ProcessId) is not null)
            {
                // Skip malformed entries and never shadow a built-in process id.
                continue;
            }

            builder[tool.ProcessId] = ToDefinition(tool);
        }

        _tools = builder.ToImmutable();
    }

    public ProcessDefinition? GetProcess(string processId)
        => _inner.GetProcess(processId) ?? (_tools.TryGetValue(processId, out var tool) ? tool : null);

    public IReadOnlyList<ProcessDefinition> ListProcesses()
        => _tools.IsEmpty ? _inner.ListProcesses() : [.. _inner.ListProcesses(), .. _tools.Values];

    public IReadOnlyList<ProcessDefinition> GetProcessesByCategory(string category)
    {
        var baseList = _inner.GetProcessesByCategory(category);
        if (_tools.IsEmpty || !string.Equals(category, CustomToolCategory, StringComparison.Ordinal))
        {
            return baseList;
        }

        return [.. baseList, .. _tools.Values];
    }

    private const string CustomToolCategory = "customcode";

    private static ProcessDefinition ToDefinition(CustomCodeLocalToolDefinition tool)
    {
        var parameters = new List<ProcessParameterSpec>(tool.Parameters.Count);
        foreach (var parameter in tool.Parameters)
        {
            if (string.IsNullOrWhiteSpace(parameter.Name))
            {
                continue;
            }

            parameters.Add(new ProcessParameterSpec
            {
                Name = parameter.Name,
                DisplayName = string.IsNullOrWhiteSpace(parameter.DisplayName) ? parameter.Name : parameter.DisplayName,
                Description = parameter.Description ?? string.Empty,
                ValueType = ProcessParameterValueType.Text,
                Required = parameter.Required,
            });
        }

        return new ProcessDefinition
        {
            ProcessId = tool.ProcessId,
            Title = string.IsNullOrWhiteSpace(tool.Title) ? tool.ProcessId : tool.Title,
            Description = string.IsNullOrWhiteSpace(tool.Description)
                ? "Operator-registered local custom-code tool."
                : tool.Description,
            Category = CustomToolCategory,
            Parameters = parameters,
            OutputArtifactKinds = [ArtifactKind.FeatureLayer],
            // Route through the custom-code fence so it is never claimed by the managed/native workers.
            RuntimeProfile = CustomCodeJobContract.RuntimeProfile,
        };
    }
}

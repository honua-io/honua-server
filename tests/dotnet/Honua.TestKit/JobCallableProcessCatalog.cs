// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.TestKit;

/// <summary>
/// Wraps a process catalog and makes selected definitions async-job-callable so tests can
/// reach a downstream concern without weakening the production catalog classification.
/// </summary>
public sealed class JobCallableProcessCatalog(
    IProcessCatalog inner,
    params string[] processIds) : IProcessCatalog
{
    private readonly HashSet<string> _processIds = processIds.ToHashSet(StringComparer.Ordinal);

    /// <inheritdoc />
    public ProcessDefinition? GetProcess(string processId)
    {
        var definition = inner.GetProcess(processId);
        return definition is null ? null : Override(definition);
    }

    /// <inheritdoc />
    public IReadOnlyList<ProcessDefinition> ListProcesses()
        => [.. inner.ListProcesses().Select(Override)];

    /// <inheritdoc />
    public IReadOnlyList<ProcessDefinition> GetProcessesByCategory(string category)
        => [.. inner.GetProcessesByCategory(category).Select(Override)];

    private ProcessDefinition Override(ProcessDefinition definition)
        => _processIds.Contains(definition.ProcessId)
            ? definition with
            {
                ExecutionKind = ProcessExecutionKind.Job,
                SupportedExecutionModes = ProcessExecutionModes.Async,
                ExecutionCapabilityReason = null
            }
            : definition;
}

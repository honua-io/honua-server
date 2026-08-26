// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// Provides the canonical runtime eligibility predicates shared by process protocol adapters.
/// </summary>
public static class ProcessExecutionEligibility
{
    /// <summary>
    /// Returns <see langword="true"/> when a process may be submitted to the shared
    /// asynchronous geoprocessing job runtime.
    /// </summary>
    public static bool IsJobCallable(ProcessDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.ExecutionKind == ProcessExecutionKind.Job
            && (definition.SupportedExecutionModes & ProcessExecutionModes.Async) != 0;
    }

    /// <summary>
    /// Returns <see langword="true"/> when a process may be submitted by the trusted
    /// workflow orchestration runtime.
    /// </summary>
    public static bool IsWorkflowCallable(ProcessDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.ExecutionKind == ProcessExecutionKind.WorkflowOnly
            && (definition.SupportedExecutionModes & ProcessExecutionModes.Async) != 0;
    }
}

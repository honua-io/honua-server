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

    /// <summary>
    /// Returns <see langword="true"/> when the catalog declares <paramref name="entryPoint"/>
    /// for <paramref name="definition"/>. This is the single predicate every advertisement
    /// surface must consult before projecting an operation: a process is offered on an
    /// entry point only when the catalog says it supports that entry point.
    /// </summary>
    public static bool Declares(ProcessDefinition definition, ProcessEntryPoints entryPoint)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return entryPoint != ProcessEntryPoints.None
            && (definition.SupportedEntryPoints & entryPoint) == entryPoint;
    }

    /// <summary>
    /// Renders declared entry points in the canonical, stable order used by the catalog
    /// documents and the evidence matrix (<c>job</c>, <c>protocol</c>, <c>workflow</c>).
    /// </summary>
    public static IReadOnlyList<string> DescribeEntryPoints(ProcessDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var names = new List<string>(3);
        if (Declares(definition, ProcessEntryPoints.Job))
        {
            names.Add("job");
        }

        if (Declares(definition, ProcessEntryPoints.Protocol))
        {
            names.Add("protocol");
        }

        if (Declares(definition, ProcessEntryPoints.Workflow))
        {
            names.Add("workflow");
        }

        return names;
    }
}

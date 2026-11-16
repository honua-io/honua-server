// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

public static class ProcessExecutionEligibility
{
    public static bool IsJobCallable(ProcessDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.ExecutionKind == ProcessExecutionKind.Job
            && (definition.SupportedExecutionModes & ProcessExecutionModes.Async) != 0
            && Declares(definition, ProcessEntryPoints.Job);
    }

    public static bool IsWorkflowCallable(ProcessDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.ExecutionKind == ProcessExecutionKind.WorkflowOnly
            && (definition.SupportedExecutionModes & ProcessExecutionModes.Async) != 0
            && Declares(definition, ProcessEntryPoints.Workflow);
    }

    public static bool Declares(ProcessDefinition definition, ProcessEntryPoints entryPoint)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return entryPoint != ProcessEntryPoints.None
            && (definition.SupportedEntryPoints & entryPoint) == entryPoint;
    }

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

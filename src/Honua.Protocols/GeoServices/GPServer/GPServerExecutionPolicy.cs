// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Protocols.GeoServices.GPServer;

internal static class GPServerExecutionPolicy
{
    public const string SynchronousExecutionType = "esriExecutionTypeSynchronous";

    public const string AsynchronousExecutionType = "esriExecutionTypeAsynchronous";

    public static bool IsJobCallable(ProcessDefinition definition)
        => ProcessExecutionEligibility.IsJobCallable(definition);

    public static bool IsSynchronous(ProcessDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return IsJobCallable(definition)
            && (definition.SupportedExecutionModes & ProcessExecutionModes.Sync) != 0;
    }

    public static string ExecutionType(ProcessDefinition definition)
        => IsSynchronous(definition) ? SynchronousExecutionType : AsynchronousExecutionType;
}

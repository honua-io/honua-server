// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Geoprocessing;

internal interface IGeoprocessingHostRuntime
{
    bool IsServerless { get; }
}

internal sealed class ProcessEnvironmentHostRuntime : IGeoprocessingHostRuntime
{
    public static ProcessEnvironmentHostRuntime Instance { get; } = new();

    public bool IsServerless => IsServerlessEnvironment(Environment.GetEnvironmentVariable);

    public static bool IsServerlessEnvironment(Func<string, string?> environmentAccessor)
    {
        ArgumentNullException.ThrowIfNull(environmentAccessor);
        return !string.IsNullOrWhiteSpace(environmentAccessor("AWS_LAMBDA_FUNCTION_NAME"))
            || !string.IsNullOrWhiteSpace(environmentAccessor("FUNCTIONS_WORKER_RUNTIME"))
            || !string.IsNullOrWhiteSpace(environmentAccessor("K_SERVICE"));
    }
}

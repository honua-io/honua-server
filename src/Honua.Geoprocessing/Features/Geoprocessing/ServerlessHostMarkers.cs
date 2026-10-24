// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Geoprocessing;

/// <summary>
/// Whether this process is a serverless runtime whose background loops stop when the
/// invocation returns. The markers match <c>SubstrateProfileResolver</c>: AWS Lambda,
/// Azure Functions, and Cloud Run.
/// </summary>
internal interface IGeoprocessingHostRuntime
{
    /// <summary>True when the process will be frozen between requests.</summary>
    bool IsServerless { get; }
}

/// <summary>Reads the process environment for the serverless runtime markers.</summary>
internal sealed class ProcessEnvironmentHostRuntime : IGeoprocessingHostRuntime
{
    /// <summary>The process-environment probe used when no test double is supplied.</summary>
    public static ProcessEnvironmentHostRuntime Instance { get; } = new();

    /// <inheritdoc />
    public bool IsServerless => IsServerlessEnvironment(Environment.GetEnvironmentVariable);

    /// <summary>True when any well-known serverless runtime marker is set.</summary>
    public static bool IsServerlessEnvironment(Func<string, string?> environmentAccessor)
    {
        ArgumentNullException.ThrowIfNull(environmentAccessor);
        return !string.IsNullOrWhiteSpace(environmentAccessor("AWS_LAMBDA_FUNCTION_NAME"))
            || !string.IsNullOrWhiteSpace(environmentAccessor("FUNCTIONS_WORKER_RUNTIME"))
            || !string.IsNullOrWhiteSpace(environmentAccessor("K_SERVICE"));
    }
}

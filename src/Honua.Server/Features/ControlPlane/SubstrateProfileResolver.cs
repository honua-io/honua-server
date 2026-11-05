// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

// Auto-detection only escalates a declaration to serverless, so an operator cannot under-declare a serverless host.
internal static class SubstrateProfileResolver
{
    // Well-known environment markers for serverless runtimes:
    //  - AWS Lambda sets AWS_LAMBDA_FUNCTION_NAME.
    //  - Azure Functions sets FUNCTIONS_WORKER_RUNTIME.
    //  - Google Cloud Run sets K_SERVICE.
    private static readonly string[] ServerlessEnvironmentMarkers =
    [
        "AWS_LAMBDA_FUNCTION_NAME",
        "FUNCTIONS_WORKER_RUNTIME",
        "K_SERVICE",
    ];

    public static BatchComputeSubstrateProfile ResolveEffectiveProfile(
        SubstrateOptions options,
        Func<string, string?> environmentAccessor)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environmentAccessor);

        if (options.Profile == BatchComputeSubstrateProfile.SingleHost
            && options.AutoDetectServerless
            && IsServerlessEnvironment(environmentAccessor))
        {
            return BatchComputeSubstrateProfile.Serverless;
        }

        return options.Profile;
    }

    public static bool IsServerlessEnvironment(Func<string, string?> environmentAccessor)
    {
        ArgumentNullException.ThrowIfNull(environmentAccessor);

        return ServerlessEnvironmentMarkers.Any(marker => !string.IsNullOrWhiteSpace(environmentAccessor(marker)));
    }

    public static string? ReadEnvironment(string name) => Environment.GetEnvironmentVariable(name);
}

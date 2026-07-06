// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

/// <summary>
/// Resolves the effective deployment substrate profile from the declared
/// <see cref="SubstrateOptions"/> plus environment auto-detection. The explicit declared profile
/// always wins when it is at least as restrictive as what auto-detection would report; auto-detection
/// only ever escalates a <see cref="BatchComputeSubstrateProfile.SingleHost"/> declaration to
/// <see cref="BatchComputeSubstrateProfile.Serverless"/> when a well-known serverless runtime is
/// present, so an operator can never accidentally under-declare a serverless host.
/// </summary>
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

    /// <summary>
    /// Resolves the effective substrate profile, escalating a single-host declaration to serverless
    /// when auto-detection is enabled and a serverless runtime marker is present.
    /// </summary>
    /// <param name="options">The declared substrate options.</param>
    /// <param name="environmentAccessor">Reads an environment variable by name (injected for tests).</param>
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

    /// <summary>
    /// Whether a well-known serverless runtime marker is present in the environment.
    /// </summary>
    public static bool IsServerlessEnvironment(Func<string, string?> environmentAccessor)
    {
        ArgumentNullException.ThrowIfNull(environmentAccessor);

        foreach (var marker in ServerlessEnvironmentMarkers)
        {
            if (!string.IsNullOrWhiteSpace(environmentAccessor(marker)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Default environment accessor reading process environment variables.</summary>
    public static string? ReadEnvironment(string name) => Environment.GetEnvironmentVariable(name);
}

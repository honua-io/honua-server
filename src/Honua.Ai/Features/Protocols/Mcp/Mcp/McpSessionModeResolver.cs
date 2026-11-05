// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp;

internal sealed record McpSessionModeSelection(McpSessionMode Mode, string Reason, string? SigningSecret)
{
    public bool Degraded { get; init; }
}

internal static class McpSessionModeResolver
{
    internal const string DeploymentModeKey = "Deployment:Mode";

    internal const string DefaultConnectionKey = "ConnectionStrings:DefaultConnection";

    internal static readonly string[] MultiInstanceEnvironmentMarkers =
    [
        "AWS_LAMBDA_FUNCTION_NAME",
        "HONUA_CONTROL_PLANE_LAMBDA_HANDLER",
        "FUNCTIONS_WORKER_RUNTIME",
        "K_SERVICE",
    ];

    public static McpSessionModeSelection Resolve(
        McpOptions options,
        bool hasSharedStore,
        IConfiguration? configuration,
        Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        if (hasSharedStore)
        {
            return new McpSessionModeSelection(McpSessionMode.Shared, "shared-store", null);
        }

        var multiInstance = DescribeMultiInstanceHost(configuration, environment);
        string reason;
        switch (options.SessionMode)
        {
            case McpSessionModeSetting.InMemory:
                return new McpSessionModeSelection(McpSessionMode.InMemory, "configured", null);
            case McpSessionModeSetting.Stateless:
                reason = "configured";
                break;
            default:
                if (multiInstance is null)
                {
                    return new McpSessionModeSelection(McpSessionMode.InMemory, "single-instance", null);
                }

                reason = multiInstance;
                break;
        }

        var secret = !string.IsNullOrWhiteSpace(options.SessionSigningKey)
            ? options.SessionSigningKey
            : configuration?[DefaultConnectionKey];
        if (string.IsNullOrWhiteSpace(secret))
        {
            return new McpSessionModeSelection(McpSessionMode.InMemory, reason + "; no-signing-secret", null)
            {
                Degraded = true
            };
        }

        return new McpSessionModeSelection(McpSessionMode.Stateless, reason, secret);
    }

    internal static string? DescribeMultiInstanceHost(IConfiguration? configuration, Func<string, string?> environment)
    {
        foreach (var marker in MultiInstanceEnvironmentMarkers)
        {
            if (!string.IsNullOrWhiteSpace(environment(marker)))
            {
                return marker;
            }
        }

        var deploymentMode = configuration?[DeploymentModeKey];
        return string.Equals(deploymentMode, "MultiNode", StringComparison.OrdinalIgnoreCase)
            ? "Deployment:Mode=MultiNode"
            : null;
    }
}

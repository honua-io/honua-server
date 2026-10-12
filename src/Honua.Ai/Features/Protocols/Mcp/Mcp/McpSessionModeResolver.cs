// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp;

/// <summary>
/// The session mode chosen for this host, why, and (for
/// <see cref="McpSessionMode.Stateless"/>) the shared secret its tokens are keyed from.
/// </summary>
/// <param name="Mode">The selected mode.</param>
/// <param name="Reason">Short, loggable reason for the selection.</param>
/// <param name="SigningSecret">Key input for stateless tokens; <c>null</c> otherwise.</param>
internal sealed record McpSessionModeSelection(McpSessionMode Mode, string Reason, string? SigningSecret)
{
    /// <summary>
    /// <c>true</c> when the host runs as several instances but sessions stay in
    /// memory because no signing secret is available: a session can then be
    /// unknown on the next instance (HTTP 404).
    /// </summary>
    public bool Degraded { get; init; }
}

/// <summary>
/// Selects how MCP sessions are kept on this host (honua-server#5629): shared
/// through a store when one is composed (Redis), stateless signed ids when the
/// host runs as several instances that cannot share memory, and in-memory on a
/// single instance.
/// </summary>
internal static class McpSessionModeResolver
{
    /// <summary>Configuration key of the deployment topology (<c>Deployment:Mode</c>).</summary>
    internal const string DeploymentModeKey = "Deployment:Mode";

    /// <summary>Configuration key of the shared database connection string.</summary>
    internal const string DefaultConnectionKey = "ConnectionStrings:DefaultConnection";

    /// <summary>
    /// Environment markers of runtimes that run the API as several execution
    /// environments behind one endpoint, with no shared memory between them:
    /// AWS Lambda (also set for the Lambda Web Adapter's web process), the
    /// control-plane Lambda handler selector, Azure Functions, and Cloud Run.
    /// </summary>
    internal static readonly string[] MultiInstanceEnvironmentMarkers =
    [
        "AWS_LAMBDA_FUNCTION_NAME",
        "HONUA_CONTROL_PLANE_LAMBDA_HANDLER",
        "FUNCTIONS_WORKER_RUNTIME",
        "K_SERVICE",
    ];

    /// <summary>
    /// Resolves the session mode for this host.
    /// </summary>
    /// <param name="options">Bound MCP options.</param>
    /// <param name="hasSharedStore">Whether an <see cref="IMcpSessionStore"/> is composed.</param>
    /// <param name="configuration">Host configuration, for <c>Deployment:Mode</c> and the connection string.</param>
    /// <param name="environment">Reads an environment variable (injected for tests).</param>
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

    /// <summary>
    /// Returns why this host runs as several instances, or <c>null</c> for a single instance.
    /// </summary>
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

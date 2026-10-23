// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.Console;

/// <summary>
/// Decides whether process-local Console stores may start. More than one
/// configured replica with a non-durable store would serve different content,
/// share tokens, and open-data pages depending on which process handled the request.
/// </summary>
public static class ConsoleStoreReplicaPolicy
{
    /// <summary>Configuration key <c>Console:ExpectedReplicaCount</c>.</summary>
    public const string ConsoleExpectedReplicaCountKey = "Console:ExpectedReplicaCount";

    /// <summary>Configuration key <c>Deployment:ReplicaCount</c>.</summary>
    public const string DeploymentReplicaCountKey = "Deployment:ReplicaCount";

    /// <summary>Environment variable <c>HONUA_SERVER_REPLICAS</c>.</summary>
    public const string ServerReplicasKey = "HONUA_SERVER_REPLICAS";

    /// <summary>
    /// Reads the first positive integer among the Console replica count, the
    /// deployment replica count, and <c>HONUA_SERVER_REPLICAS</c>. Missing or
    /// non-positive values are skipped. The default is one replica.
    /// </summary>
    public static int ResolveExpectedReplicas(
        string? consoleExpectedReplicaCount,
        string? deploymentReplicaCount,
        string? serverReplicas)
    {
        foreach (var raw in new[] { consoleExpectedReplicaCount, deploymentReplicaCount, serverReplicas })
        {
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0)
            {
                return value;
            }
        }

        return 1;
    }

    /// <summary>
    /// Throws when the stores are not durable and more than one replica is configured.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Process-local Console content, share, and open-data stores cannot be shared
    /// across the configured replica count.
    /// </exception>
    public static void EnsureSingleReplicaWhenNotDurable(bool durable, int expectedReplicas)
    {
        if (durable || expectedReplicas <= 1)
        {
            return;
        }

        throw new InvalidOperationException(
            "Console content, share, and open-data stores are process-local and are not shared across replicas. "
            + "Configured replica count is " + expectedReplicas.ToString(CultureInfo.InvariantCulture)
            + ". Use a durable store or run a single replica.");
    }
}

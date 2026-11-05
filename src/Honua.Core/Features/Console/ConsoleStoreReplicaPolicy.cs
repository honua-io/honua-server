// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.Console;

public static class ConsoleStoreReplicaPolicy
{
    public const string ConsoleExpectedReplicaCountKey = "Console:ExpectedReplicaCount";

    public const string DeploymentReplicaCountKey = "Deployment:ReplicaCount";

    public const string ServerReplicasKey = "HONUA_SERVER_REPLICAS";

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

    // More than one replica with a process-local store would serve different content, share tokens, and open-data pages.
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

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Honua.Core.Configuration;

// How many server tasks may share one database at once: the largest stated count, an autoscaling ceiling included,
// because pools and refusals must hold at the peak. A multi-node deployment that states none is taken to run more than
// one task, because state that only one task holds is wrong there either way.
public static class DeploymentTaskCount
{
    public const string ReplicaCountKey = "Deployment:ReplicaCount";

    public const string ServerReplicasKey = "HONUA_SERVER_REPLICAS";

    public const string MaxTasksKey = "Deployment:MaxTasks";

    public const string ModeKey = "Deployment:Mode";

    internal const int UnstatedMultiNodeTaskCount = 2;

    public static int Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var stated = new[] { ReplicaCountKey, ServerReplicasKey, MaxTasksKey }
            .Select(key => TryReadCount(configuration[key], out var count) ? count : 0)
            .Max();
        if (stated > 0)
        {
            return stated;
        }

        return string.Equals(configuration[ModeKey], "MultiNode", StringComparison.OrdinalIgnoreCase)
            ? UnstatedMultiNodeTaskCount
            : 1;
    }

    private static bool TryReadCount(string? raw, out int count)
        => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out count) && count > 0;
}

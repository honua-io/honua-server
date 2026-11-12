// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Honua.Core.Configuration;

// How many server tasks may share one database at once. An explicit count wins, and an autoscaling ceiling counts as
// the number of tasks; a multi-node deployment that states neither is taken to run more than one task, because state
// that only one task holds is wrong there either way.
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
        if (TryReadCount(configuration[ReplicaCountKey], out var count)
            || TryReadCount(configuration[ServerReplicasKey], out count)
            || TryReadCount(configuration[MaxTasksKey], out count))
        {
            return count;
        }

        return string.Equals(configuration[ModeKey], "MultiNode", StringComparison.OrdinalIgnoreCase)
            ? UnstatedMultiNodeTaskCount
            : 1;
    }

    private static bool TryReadCount(string? raw, out int count)
        => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out count) && count > 0;
}

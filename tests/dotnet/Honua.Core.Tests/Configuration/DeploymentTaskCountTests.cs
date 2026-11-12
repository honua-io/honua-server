// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;

namespace Honua.Core.Tests.Configuration;

public sealed class DeploymentTaskCountTests
{
    [UnitTheory]
    [InlineData(null, null, null, null, 1)]
    [InlineData(null, null, null, "SingleInstance", 1)]
    [InlineData(null, null, null, "MultiNode", 2)]
    [InlineData(null, null, "1", "MultiNode", 1)]
    [InlineData(null, null, "8", "MultiNode", 8)]
    [InlineData("1", null, null, "MultiNode", 1)]
    [InlineData("6", null, null, null, 6)]
    [InlineData(null, "3", "9", null, 3)]
    [InlineData("4", "9", "10", "MultiNode", 4)]
    [InlineData("0", "5", null, null, 5)]
    [InlineData("many", null, null, "multinode", 2)]
    public void Resolve_StatedCountOrMode_ReturnsExpectedTasks(
        string? replicaCount, string? serverReplicas, string? maxTasks, string? mode, int expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DeploymentTaskCount.ReplicaCountKey] = replicaCount,
            [DeploymentTaskCount.ServerReplicasKey] = serverReplicas,
            [DeploymentTaskCount.MaxTasksKey] = maxTasks,
            [DeploymentTaskCount.ModeKey] = mode,
        }).Build();

        DeploymentTaskCount.Resolve(configuration).Should().Be(expected);
    }
}

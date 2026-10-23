// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Console;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Console;

public sealed class ConsoleStoreReplicaPolicyTests
{
    [UnitTest]
    public void Issue5635_ResolveExpectedReplicas_PrefersTheFirstPositiveValue()
    {
        ConsoleStoreReplicaPolicy.ResolveExpectedReplicas("2", "4", "8").Should().Be(2);
        ConsoleStoreReplicaPolicy.ResolveExpectedReplicas("0", "nope", "3").Should().Be(3);
        ConsoleStoreReplicaPolicy.ResolveExpectedReplicas(null, "", null).Should().Be(1);
    }

    [UnitTest]
    public void Issue5635_EnsureSingleReplicaWhenNotDurable_RejectsMultipleReplicas()
    {
        var act = () => ConsoleStoreReplicaPolicy.EnsureSingleReplicaWhenNotDurable(durable: false, expectedReplicas: 2);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage(
                "Console content, share, and open-data stores are process-local and are not shared across replicas. "
                + "Configured replica count is 2. Use a durable store or run a single replica.");
    }

    [UnitTest]
    public void Issue5635_EnsureSingleReplicaWhenNotDurable_AllowsOneReplicaOrADurableStore()
    {
        ConsoleStoreReplicaPolicy.EnsureSingleReplicaWhenNotDurable(durable: false, expectedReplicas: 1);
        ConsoleStoreReplicaPolicy.EnsureSingleReplicaWhenNotDurable(durable: true, expectedReplicas: 3);
    }
}

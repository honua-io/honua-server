// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Tests.Features.ControlPlane;

public sealed class ExecutionJobStatusExtensionsTests
{
    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(ExecutionJobStatus.Queued, false)]
    [InlineData(ExecutionJobStatus.Provisioning, false)]
    [InlineData(ExecutionJobStatus.Running, false)]
    [InlineData(ExecutionJobStatus.Succeeded, true)]
    [InlineData(ExecutionJobStatus.Failed, true)]
    [InlineData(ExecutionJobStatus.Cancelled, true)]
    [InlineData((ExecutionJobStatus)(-1), false)]
    [InlineData((ExecutionJobStatus)int.MaxValue, false)]
    public void IsTerminal_OnlyCompletedOutcomesAreTerminal(ExecutionJobStatus status, bool expected)
    {
        Assert.Equal(expected, status.IsTerminal());
    }
}

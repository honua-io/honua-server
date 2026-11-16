// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using NSubstitute;

namespace Honua.TestKit.Helpers;

public static class JobStoreTestExtensions
{
    public static IExecutionJobStore WithTrySet(this IExecutionJobStore mock)
    {
        mock.TrySetAsync(
            Arg.Any<ExecutionJobRecord>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>())
        .Returns(true);
        return mock;
    }
}

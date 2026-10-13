// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using NSubstitute;

namespace Honua.TestKit.Helpers;

/// <summary>
/// Configures <see cref="IExecutionJobStore"/> NSubstitute mocks so that
/// <see cref="IExecutionJobStore.TrySetAsync"/> delegates to the existing
/// <see cref="IExecutionJobStore.SetAsync"/> mock and returns <c>true</c>.
/// </summary>
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

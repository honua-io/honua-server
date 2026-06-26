// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing.CustomCode;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Proves the custom-code dispatch executor is fenced to the <c>custom-code</c>
/// runtime profile (so neither the lean managed dispatcher nor the native GDAL
/// worker can claim a custom-code job) and fails closed when claimed in-process.
/// </summary>
[Protocol(TestProtocols.GPServer)]
public sealed class CustomCodeDispatchExecutorTests
{
    private readonly CustomCodeDispatchJobExecutor _sut = new(NullLogger<CustomCodeDispatchJobExecutor>.Instance);

    [UnitTest]
    [Operation(Operations.Query)]
    public void Executor_AcceptsOnlyCustomCodeProfile()
    {
        _sut.Kind.Should().Be(ExecutionJobKind.Geoprocessing);
        _sut.AcceptedRuntimeProfiles.Should().Equal(CustomCodeJobContract.RuntimeProfile);

        // Claim fence: a managed or native job is NOT claimable by this executor, and
        // a custom-code job is NOT claimable by a default (managed-only) executor.
        RuntimeProfiles.CanClaim(_sut.AcceptedRuntimeProfiles, RuntimeProfiles.Managed).Should().BeFalse();
        RuntimeProfiles.CanClaim(_sut.AcceptedRuntimeProfiles, RuntimeProfiles.Native).Should().BeFalse();
        RuntimeProfiles.CanClaim(_sut.AcceptedRuntimeProfiles, CustomCodeJobContract.RuntimeProfile).Should().BeTrue();
        RuntimeProfiles.CanClaim(RuntimeProfiles.DefaultAccepted, CustomCodeJobContract.RuntimeProfile).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task Executor_ClaimedInProcess_FailsClosed()
    {
        var job = new ExecutionJobRecord
        {
            OperationId = "gp-cc-1",
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = "aws-batch",
                WorkloadName = "customcode",
                RuntimeProfile = CustomCodeJobContract.RuntimeProfile
            }
        };

        var result = await _sut.ExecuteAsync(
            job, Substitute.For<IJobExecutionContext>(), CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("Batch");
    }
}

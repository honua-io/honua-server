// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing.CustomCode;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Geoprocessing;

public sealed class CustomCodeArtifactRetryGuardTests
{
    [UnitTest]
    public void Retry_ExactRemainingJournalBudget_RetainsAllPendingAndAvailableCharges()
    {
        var references = new[] { Receipt(0, 64L << 20), Receipt(1, 64L << 20), Receipt(2, 64L << 20), Receipt(3, 63L << 20) };
        var decision = new CustomCodeArtifactRetryGuard().EvaluateRetry(Job(references))!;
        decision.Allowed.Should().BeTrue();
        decision.ArtifactReferences.Should().Equal(references);
    }

    [UnitTest]
    public void Retry_ByteCapacityCannotFitMandatoryJournal_IsRefused()
    {
        var references = new[] { Receipt(0, 64L << 20), Receipt(1, 64L << 20), Receipt(2, 64L << 20), Receipt(3, (63L << 20) + 1) };
        var decision = new CustomCodeArtifactRetryGuard().EvaluateRetry(Job(references))!;
        decision.Allowed.Should().BeFalse();
        decision.RefusalReason.Should().Contain("cumulative artifact capacity");
    }

    [UnitTest]
    public void Retry_CountCapacityCannotFitMandatoryJournal_IsRefused()
    {
        var references = Enumerable.Range(0, 32).Select(index => Receipt(index, 0)).ToArray();
        new CustomCodeArtifactRetryGuard().EvaluateRetry(Job(references))!.Allowed.Should().BeFalse();
    }

    [UnitTest]
    public void Retry_InvalidBrokerReceipt_IsRefusedWithoutResettingBudget()
    {
        var reference = Receipt(0, 1).Replace("broker-job", "other-job", StringComparison.Ordinal);
        new CustomCodeArtifactRetryGuard().EvaluateRetry(Job([reference]))!.Allowed.Should().BeFalse();
    }

    private static string Receipt(int index, long bytes) => new CustomCodeArtifactDescriptor
    {
        JobId = "broker-job",
        Attempt = 1,
        Name = $"output{index}.bin",
        ObjectKey = $"gp/custom-artifacts/broker-job/a1/output{index}.bin",
        ContentType = "application/octet-stream",
        SizeBytes = bytes,
        State = index % 2 == 0 ? "pending" : "available",
    }.Serialize();

    private static ExecutionJobRecord Job(IReadOnlyList<string> references) => new()
    {
        OperationId = "broker-job",
        Status = ExecutionJobStatus.Failed,
        AttemptCount = 1,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        ArtifactReferences = references,
        Spec = new ExecutionJobSpec
        {
            Kind = ExecutionJobKind.Geoprocessing,
            TargetKind = BatchComputeTargetKind.AwsBatch,
            Backend = "aws-batch",
            WorkloadName = "custom-code",
            RuntimeProfile = CustomCodeJobContract.RuntimeProfile,
        },
    };
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.TestKit.Attributes;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Unit tests for geoprocessing proto/domain conversion helpers.
/// </summary>
public sealed class GeoprocessingConversionHelpersTests
{
    // -----------------------------------------------------------------------
    // ToDomainPlan
    // -----------------------------------------------------------------------

    [UnitTest]
    public void ToDomainPlan_WithValidProto_ConvertsPlanFields()
    {
        var proto = new Proto.AnalysisPlan
        {
            PlanId = "plan-1",
            IntentId = "intent-1"
        };
        proto.Steps.Add(new Proto.AnalysisPlanStep
        {
            StepId = "step-1",
            Kind = Proto.PlanStepKind.Geoprocess,
            ProcessId = "buffer"
        });
        proto.Outputs.Add(Proto.ArtifactKind.FeatureLayer);
        proto.Warnings.Add("test-warning");

        var result = GeoprocessingConversionHelpers.ToDomainPlan(proto);

        result.PlanId.Should().Be("plan-1");
        result.IntentId.Should().Be("intent-1");
        result.Steps.Should().HaveCount(1);
        result.Steps[0].StepId.Should().Be("step-1");
        result.Steps[0].Kind.Should().Be(AnalysisPlanStepKind.Geoprocess);
        result.Steps[0].ProcessId.Should().Be("buffer");
        result.Outputs.Should().ContainSingle().Which.Should().Be(ArtifactKind.FeatureLayer);
        result.Warnings.Should().ContainSingle().Which.Should().Be("test-warning");
    }

    [UnitTest]
    public void ToDomainPlan_WithStepInputs_ConvertsDictionary()
    {
        var proto = new Proto.AnalysisPlan
        {
            PlanId = "plan-1",
            IntentId = "intent-1"
        };
        var step = new Proto.AnalysisPlanStep
        {
            StepId = "step-1",
            Kind = Proto.PlanStepKind.QueryFeatures
        };
        step.Inputs["layer"] = "parcels";
        step.Inputs["where"] = "area > 100";
        step.DependsOn.Add("step-0");
        proto.Steps.Add(step);

        var result = GeoprocessingConversionHelpers.ToDomainPlan(proto);

        result.Steps[0].Inputs.Should().HaveCount(2);
        result.Steps[0].Inputs["layer"].Should().Be("parcels");
        result.Steps[0].DependsOn.Should().ContainSingle().Which.Should().Be("step-0");
    }

    [UnitTest]
    public void ToDomainPlan_WithEmptySteps_ReturnsEmptyList()
    {
        var proto = new Proto.AnalysisPlan
        {
            PlanId = "plan-1",
            IntentId = "intent-1"
        };

        var result = GeoprocessingConversionHelpers.ToDomainPlan(proto);

        result.Steps.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // ToProtoValidatePlanResponse
    // -----------------------------------------------------------------------

    [UnitTest]
    public void ToProtoValidatePlanResponse_WithViolations_MapsAll()
    {
        var result = new PlanValidationResult
        {
            IsExecutable = false,
            RequiresApproval = true,
            Violations =
            [
                new GeoprocessingValidationFailure
                {
                    Code = "MISSING_FIELD",
                    Message = "Field is required",
                    FieldPath = "plan.steps[0].process_id"
                }
            ],
            Warnings = ["Some warning"]
        };

        var response = GeoprocessingConversionHelpers.ToProtoValidatePlanResponse(result);

        response.IsExecutable.Should().BeFalse();
        response.RequiresApproval.Should().BeTrue();
        response.Violations.Should().HaveCount(1);
        response.Violations[0].Code.Should().Be("MISSING_FIELD");
        response.Violations[0].FieldPath.Should().Be("plan.steps[0].process_id");
        response.Warnings.Should().ContainSingle().Which.Should().Be("Some warning");
    }

    // -----------------------------------------------------------------------
    // ToProtoDryRunPlanResponse
    // -----------------------------------------------------------------------

    [UnitTest]
    public void ToProtoDryRunPlanResponse_MapsAllFields()
    {
        var result = new DryRunResult
        {
            EstimatedDurationSeconds = 42.5,
            EstimatedArtifacts = [ArtifactKind.FeatureLayer, ArtifactKind.Map],
            SideEffects = ["Creates temporary layer"]
        };

        var response = GeoprocessingConversionHelpers.ToProtoDryRunPlanResponse(result);

        response.EstimatedDurationSeconds.Should().Be(42.5);
        response.EstimatedArtifacts.Should().HaveCount(2);
        response.EstimatedArtifacts[0].Should().Be(Proto.ArtifactKind.FeatureLayer);
        response.EstimatedArtifacts[1].Should().Be(Proto.ArtifactKind.Map);
        response.SideEffects.Should().ContainSingle().Which.Should().Be("Creates temporary layer");
    }

    // -----------------------------------------------------------------------
    // ToProtoExecutionJob
    // -----------------------------------------------------------------------

    [UnitTest]
    public void ToProtoExecutionJob_MapsAllFields()
    {
        var now = DateTimeOffset.UtcNow;
        var job = new ExecutionJobRecord
        {
            OperationId = "job-1",
            Status = ExecutionJobStatus.Running,
            PercentComplete = 50.0,
            CurrentPhase = "Executing step 2",
            CreatedAt = now.AddMinutes(-5),
            UpdatedAt = now,
            ErrorMessage = null,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "test"
            }
        };

        var proto = GeoprocessingConversionHelpers.ToProtoExecutionJob(job);

        proto.JobId.Should().Be("job-1");
        proto.Status.Should().Be(Proto.JobStatus.Running);
        proto.PercentComplete.Should().Be(50.0);
        proto.CurrentPhase.Should().Be("Executing step 2");
        proto.HasCompletedAt.Should().BeFalse();
        proto.HasErrorMessage.Should().BeFalse();
    }

    [UnitTest]
    public void ToProtoExecutionJob_WithTerminalStatus_MapsCompletedAt()
    {
        var now = DateTimeOffset.UtcNow;
        var job = new ExecutionJobRecord
        {
            OperationId = "job-1",
            Status = ExecutionJobStatus.Failed,
            CreatedAt = now.AddMinutes(-10),
            UpdatedAt = now,
            CompletedAt = now,
            ErrorMessage = "Step 2 failed",
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "test"
            }
        };

        var proto = GeoprocessingConversionHelpers.ToProtoExecutionJob(job);

        proto.Status.Should().Be(Proto.JobStatus.Failed);
        proto.HasCompletedAt.Should().BeTrue();
        proto.ErrorMessage.Should().Be("Step 2 failed");
    }

    // -----------------------------------------------------------------------
    // Enum conversions
    // -----------------------------------------------------------------------

    [UnitTest]
    public void ToProtoExecutionJob_MapsAllJobStatuses()
    {
        var statuses = new[]
        {
            (ExecutionJobStatus.Queued, Proto.JobStatus.Queued),
            (ExecutionJobStatus.Provisioning, Proto.JobStatus.Provisioning),
            (ExecutionJobStatus.Running, Proto.JobStatus.Running),
            (ExecutionJobStatus.Succeeded, Proto.JobStatus.Succeeded),
            (ExecutionJobStatus.Failed, Proto.JobStatus.Failed),
            (ExecutionJobStatus.Cancelled, Proto.JobStatus.Cancelled)
        };

        foreach (var (domain, expected) in statuses)
        {
            var job = CreateMinimalJob(domain);
            var proto = GeoprocessingConversionHelpers.ToProtoExecutionJob(job);
            proto.Status.Should().Be(expected, $"domain {domain} should map to proto {expected}");
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static ExecutionJobRecord CreateMinimalJob(ExecutionJobStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = "job-test",
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "test"
            }
        };
    }
}

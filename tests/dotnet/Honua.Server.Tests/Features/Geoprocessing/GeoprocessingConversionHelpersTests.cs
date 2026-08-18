// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Geoprocessing;
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
        var proto = new Proto.ExecutionPlan
        {
            PlanId = "plan-1",
            SpecVersion = "intent-1"
        };
        proto.Steps.Add(new Proto.PlanStep
        {
            StepId = "step-1",
            Kind = "geoprocess",
            Inputs =
            {
                ["processId"] = GeoprocessingConversionHelpers.ToProtoParameterValue("buffer")
            }
        });
        proto.ExpectedOutputs.Add("feature_layer");

        var result = GeoprocessingConversionHelpers.ToDomainPlan(proto);

        result.PlanId.Should().Be("plan-1");
        result.IntentId.Should().Be("intent-1");
        result.Steps.Should().HaveCount(1);
        result.Steps[0].StepId.Should().Be("step-1");
        result.Steps[0].Kind.Should().Be(AnalysisPlanStepKind.Geoprocess);
        result.Steps[0].ProcessId.Should().Be("buffer");
        result.Outputs.Should().ContainSingle().Which.Should().Be(ArtifactKind.FeatureLayer);
        result.Warnings.Should().BeEmpty();
    }

    [UnitTest]
    public void ToDomainPlan_WithStepInputs_ConvertsDictionary()
    {
        var proto = new Proto.ExecutionPlan
        {
            PlanId = "plan-1",
            SpecVersion = "intent-1"
        };
        var step = new Proto.PlanStep
        {
            StepId = "step-1",
            Kind = "query_features"
        };
        step.Inputs["layer"] = GeoprocessingConversionHelpers.ToProtoParameterValue("parcels");
        step.Inputs["where"] = GeoprocessingConversionHelpers.ToProtoParameterValue("area > 100");
        step.Dependencies.Add("step-0");
        proto.Steps.Add(step);

        var result = GeoprocessingConversionHelpers.ToDomainPlan(proto);

        result.Steps[0].Inputs.Should().HaveCount(2);
        result.Steps[0].Inputs["layer"].Should().Be("parcels");
        result.Steps[0].DependsOn.Should().ContainSingle().Which.Should().Be("step-0");
    }

    [UnitTest]
    public void ToDomainPlan_WithEmptySteps_ReturnsEmptyList()
    {
        var proto = new Proto.ExecutionPlan
        {
            PlanId = "plan-1",
            SpecVersion = "intent-1"
        };

        var result = GeoprocessingConversionHelpers.ToDomainPlan(proto);

        result.Steps.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // ToProtoValidateResponse
    // -----------------------------------------------------------------------

    [UnitTest]
    public void ToProtoValidateResponse_WithViolations_MapsAll()
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

        var response = GeoprocessingConversionHelpers.ToProtoValidateResponse(result);

        response.Valid.Should().BeFalse();
        response.Issues.Should().HaveCount(2);
        response.Issues[0].Message.Should().Be("Field is required");
        response.Issues[0].Field.Should().Be("plan.steps[0].process_id");
        response.Issues[0].Severity.Should().Be(Proto.Severity.Error);
        response.Issues[1].Message.Should().Be("Some warning");
        response.Issues[1].Severity.Should().Be(Proto.Severity.Warning);
    }

    // -----------------------------------------------------------------------
    // ToProtoDryRunResponse
    // -----------------------------------------------------------------------

    [UnitTest]
    public void ToProtoDryRunResponse_MapsAllFields()
    {
        var result = new DryRunResult
        {
            EstimatedDurationSeconds = 42.5,
            EstimatedArtifacts = [ArtifactKind.FeatureLayer, ArtifactKind.Map],
            SideEffects = ["Creates temporary layer"]
        };

        var response = GeoprocessingConversionHelpers.ToProtoDryRunResponse(result);

        response.Valid.Should().BeTrue();
        response.Result.EstimatedDurationSeconds.Should().Be(43);
        response.Result.EstimatedArtifacts.Should().HaveCount(2);
        response.Result.EstimatedArtifacts[0].ArtifactClass.Should().Be(Proto.ArtifactClass.FeatureLayer);
        response.Result.EstimatedArtifacts[1].ArtifactClass.Should().Be(Proto.ArtifactClass.Map);
        response.Result.SideEffects.Should().ContainSingle().Which.Description.Should().Be("Creates temporary layer");
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

        var proto = GeoprocessingConversionHelpers.ToProtoGetJobResponse(job);

        proto.JobId.Should().Be("job-1");
        proto.State.Should().Be(Proto.JobState.Running);
        proto.Progress.ProgressPercent.Should().Be(50);
        proto.Progress.Message.Should().Be("Executing step 2");
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

        var proto = GeoprocessingConversionHelpers.ToProtoGetJobResponse(job);

        proto.State.Should().Be(Proto.JobState.Failed);
        proto.Progress.UpdatedAt.Should().Be(now.ToUnixTimeMilliseconds());
    }

    [UnitTest]
    public void ToProtoGetJobResult_AvailableStagedArtifact_UsesProjectedContentRoute()
    {
        const string contentRoute = "/api/geoprocessing/jobs/job-1/artifacts/0/content";
        var package = AnalysisResultPackage.CreateCompleted(
            "job-1:v1",
            new ResultSummary { Title = "Result" },
            [new ArtifactRef
            {
                ArtifactId = "job-1:artifact:1",
                Kind = ArtifactKind.Raster,
                Label = "outputRaster",
                Uri = contentRoute,
                Metadata = new Dictionary<string, string>
                {
                    [RasterOutputArtifactMetadata.ContentRoute] = contentRoute,
                },
            }],
            [],
            new ProvenanceRecord { Sources = [], ProcessDefinitions = [] });

        var proto = GeoprocessingConversionHelpers.ToProtoGetJobResultResponse("job-1", package);

        proto.Result.Artifacts.Should().ContainSingle()
            .Which.ProducerRef.Should().Be(contentRoute);
    }

    [UnitTest]
    public void ToProtoGetJobResult_UnavailableStagedArtifact_DoesNotUseMetadataRoute()
    {
        const string contentRoute = "/api/geoprocessing/jobs/job-1/artifacts/0/content";
        var package = AnalysisResultPackage.CreateCompleted(
            "job-1:v1",
            new ResultSummary { Title = "Result" },
            [new ArtifactRef
            {
                ArtifactId = "job-1:artifact:1",
                Kind = ArtifactKind.Raster,
                Label = "outputRaster",
                Metadata = new Dictionary<string, string>
                {
                    [RasterOutputArtifactMetadata.ContentRoute] = contentRoute,
                },
            }],
            [],
            new ProvenanceRecord { Sources = [], ProcessDefinitions = [] });

        var proto = GeoprocessingConversionHelpers.ToProtoGetJobResultResponse("job-1", package);

        proto.Result.Artifacts.Should().ContainSingle()
            .Which.ProducerRef.Should().Be("outputRaster");
    }

    [UnitTest]
    public void ToProtoGetJobResult_Failed_CarriesNumericCodeAndSymbolicDetail()
    {
        // Geospatial.Grpc 0.2.0-alpha.1 replaced ErrorDetail's `string error_code` with an
        // `int32 code`. The numeric code is a stable per-kind assignment and the symbolic name
        // it replaced is preserved in details["error_code"] so nothing on the wire is lost.
        var package = AnalysisResultPackage.CreateFailed(
            "job-1:v1",
            new ResultSummary { Title = "Failed" },
            [new GeoprocessingError
            {
                Kind = GeoprocessingErrorKind.UnknownDataset,
                Message = "Dataset not found.",
                StepId = "step-1"
            }],
            new ProvenanceRecord { Sources = [], ProcessDefinitions = [] });

        var proto = GeoprocessingConversionHelpers.ToProtoGetJobResultResponse("job-1", package);

        proto.Error.Code.Should().Be(1002);
        proto.Error.Details.Should().ContainKey("error_code")
            .WhoseValue.Should().Be("UNKNOWN_DATASET");
        proto.Error.Message.Should().Be("Dataset not found.");
        proto.Error.NodeId.Should().Be("step-1");
    }

    [UnitTest]
    public void ToProtoGetJobResult_FailedWithNoErrors_SynthesizesExecutionFailedDetail()
    {
        var package = AnalysisResultPackage.CreateFailed(
            "job-1:v1",
            new ResultSummary { Title = "Failed", Description = "Backend went away." },
            [],
            new ProvenanceRecord { Sources = [], ProcessDefinitions = [] });

        var proto = GeoprocessingConversionHelpers.ToProtoGetJobResultResponse("job-1", package);

        proto.Error.Code.Should().Be(1004);
        proto.Error.Details.Should().ContainKey("error_code")
            .WhoseValue.Should().Be("EXECUTION_FAILED");
        proto.Error.Category.Should().Be(Proto.ErrorCategory.Execution);
        proto.Error.Retryability.Should().Be(Proto.Retryability.TransientBackendError);
    }

    // -----------------------------------------------------------------------
    // Enum conversions
    // -----------------------------------------------------------------------

    [UnitTest]
    public void ToProtoExecutionJob_MapsAllJobStatuses()
    {
        var statuses = new[]
        {
            (ExecutionJobStatus.Queued, Proto.JobState.Validated),
            (ExecutionJobStatus.Provisioning, Proto.JobState.Running),
            (ExecutionJobStatus.Running, Proto.JobState.Running),
            (ExecutionJobStatus.Succeeded, Proto.JobState.Completed),
            (ExecutionJobStatus.Failed, Proto.JobState.Failed),
            (ExecutionJobStatus.Cancelled, Proto.JobState.Cancelled)
        };

        foreach (var (domain, expected) in statuses)
        {
            var job = CreateMinimalJob(domain);
            var proto = GeoprocessingConversionHelpers.ToProtoGetJobResponse(job);
            proto.State.Should().Be(expected, $"domain {domain} should map to proto {expected}");
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

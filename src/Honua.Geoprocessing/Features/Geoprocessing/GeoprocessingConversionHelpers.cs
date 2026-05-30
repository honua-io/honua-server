// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Proto = Geospatial.V1;
using DomainPlan = Honua.Core.Features.Geoprocessing.Domain.AnalysisPlan;
using DomainPlanStep = Honua.Core.Features.Geoprocessing.Domain.AnalysisPlanStep;

namespace Honua.Geoprocessing;

/// <summary>
/// Stateless conversion helpers between geoprocessing domain types and proto messages.
/// </summary>
internal static class GeoprocessingConversionHelpers
{
    // -----------------------------------------------------------------------
    // Proto → Domain
    // -----------------------------------------------------------------------

    /// <summary>
    /// Converts a proto ExecutionPlan to the domain representation.
    /// </summary>
    public static DomainPlan ToDomainPlan(Proto.ExecutionPlan proto)
    {
        var steps = proto.Steps.Count == 0
            ? (IReadOnlyList<DomainPlanStep>)[]
            : proto.Steps.Select(ToDomainPlanStep).ToArray();

        var outputs = proto.ExpectedOutputs.Count == 0
            ? (IReadOnlyList<ArtifactKind>)[]
            : proto.ExpectedOutputs.Select(ToDomainArtifactKind).ToArray();

        return new DomainPlan
        {
            PlanId = proto.PlanId,
            IntentId = string.IsNullOrWhiteSpace(proto.SpecVersion) ? proto.PlanId : proto.SpecVersion,
            Steps = steps,
            Outputs = outputs,
            Warnings = []
        };
    }

    private static DomainPlanStep ToDomainPlanStep(Proto.PlanStep proto)
    {
        var inputs = proto.Inputs.Count == 0
            ? new Dictionary<string, string>()
            : proto.Inputs
                .Where(kv => !IsProcessIdInput(kv.Key))
                .ToDictionary(kv => kv.Key, kv => ToDomainInputValue(kv.Value), StringComparer.Ordinal);

        return new DomainPlanStep
        {
            StepId = proto.StepId,
            Kind = ToDomainPlanStepKind(proto.Kind),
            ProcessId = ResolveProcessId(proto),
            Inputs = inputs,
            DependsOn = proto.Dependencies.Count == 0 ? [] : proto.Dependencies.ToArray()
        };
    }

    // -----------------------------------------------------------------------
    // Domain → Proto
    // -----------------------------------------------------------------------

    /// <summary>
    /// Converts a domain PlanValidationResult to the proto response.
    /// </summary>
    public static Proto.ValidatePlanResponse ToProtoValidatePlanResponse(PlanValidationResult result)
    {
        var response = new Proto.ValidatePlanResponse
        {
            Valid = result.IsExecutable
        };

        foreach (var violation in result.Violations)
        {
            response.Issues.Add(ToProtoPlanValidationIssue(violation, Proto.IssueSeverity.Error));
        }

        foreach (var warning in result.Warnings)
        {
            response.Issues.Add(new Proto.PlanValidationIssue
            {
                Message = warning,
                Severity = Proto.IssueSeverity.Warning
            });
        }

        return response;
    }

    /// <summary>
    /// Converts a domain DryRunResult to the proto response.
    /// </summary>
    public static Proto.DryRunPlanResponse ToProtoDryRunPlanResponse(DryRunResult result)
    {
        var response = new Proto.DryRunPlanResponse
        {
            Valid = true,
            Result = new Proto.DryRunResult
            {
                EstimatedDurationSeconds = (long)Math.Ceiling(result.EstimatedDurationSeconds)
            }
        };

        foreach (var artifact in result.EstimatedArtifacts)
        {
            response.Result.EstimatedArtifacts.Add(new Proto.EstimatedArtifact
            {
                ArtifactClass = ToProtoArtifactClass(artifact)
            });
        }

        foreach (var sideEffect in result.SideEffects)
        {
            response.Result.SideEffects.Add(new Proto.SideEffect { Description = sideEffect });
        }

        return response;
    }

    /// <summary>
    /// Converts a non-executable validation result to the proto dry-run response.
    /// </summary>
    public static Proto.DryRunPlanResponse ToProtoDryRunPlanResponse(PlanValidationResult result)
    {
        var response = new Proto.DryRunPlanResponse
        {
            Valid = result.IsExecutable
        };

        foreach (var violation in result.Violations)
        {
            response.Issues.Add(ToProtoPlanValidationIssue(violation, Proto.IssueSeverity.Error));
        }

        foreach (var warning in result.Warnings)
        {
            response.Issues.Add(new Proto.PlanValidationIssue
            {
                Message = warning,
                Severity = Proto.IssueSeverity.Warning
            });
        }

        return response;
    }

    /// <summary>
    /// Converts an ExecutionJobRecord to the proto submit response.
    /// </summary>
    public static Proto.SubmitJobResponse ToProtoSubmitJobResponse(ExecutionJobRecord job)
        => new()
        {
            JobId = job.OperationId,
            State = ToProtoJobState(job.Status)
        };

    /// <summary>
    /// Converts an ExecutionJobRecord to the proto get response.
    /// </summary>
    public static Proto.GetJobResponse ToProtoGetJobResponse(ExecutionJobRecord job)
        => new()
        {
            JobId = job.OperationId,
            State = ToProtoJobState(job.Status),
            Progress = ToProtoJobProgress(job)
        };

    /// <summary>
    /// Converts a domain AnalysisResultPackage to the proto job-result response.
    /// </summary>
    public static Proto.GetJobResultResponse ToProtoGetJobResultResponse(string jobId, AnalysisResultPackage package)
    {
        var response = new Proto.GetJobResultResponse { JobId = jobId };

        if (package.Status is GeoprocessingWorkflowStatus.Failed or GeoprocessingWorkflowStatus.Cancelled)
        {
            response.Error = package.Errors.Count == 0
                ? new Proto.ErrorDetail
                {
                    ErrorCode = package.Status == GeoprocessingWorkflowStatus.Cancelled ? "CANCELLED" : "EXECUTION_FAILED",
                    Category = Proto.ErrorCategory.Execution,
                    Message = package.Summary.Description ?? package.Summary.Title,
                    Retryability = package.Status == GeoprocessingWorkflowStatus.Cancelled
                        ? Proto.Retryability.FixPlanAndRetry
                        : Proto.Retryability.TransientBackendError
                }
                : ToProtoErrorDetail(package.Errors[0]);
            return response;
        }

        response.Result = ToProtoExecutionResult(package);
        return response;
    }

    // -----------------------------------------------------------------------
    // Individual type conversions
    // -----------------------------------------------------------------------

    private static Proto.JobProgress ToProtoJobProgress(ExecutionJobRecord job)
    {
        var progress = new Proto.JobProgress
        {
            JobId = job.OperationId,
            State = ToProtoJobState(job.Status),
            ProgressPercent = job.PercentComplete.HasValue
                ? (int)Math.Clamp(Math.Round(job.PercentComplete.Value), 0, 100)
                : 0,
            StartedAt = job.CreatedAt.ToUnixTimeMilliseconds(),
            UpdatedAt = job.UpdatedAt.ToUnixTimeMilliseconds()
        };

        if (!string.IsNullOrWhiteSpace(job.CurrentPhase))
        {
            progress.Message = job.CurrentPhase;
        }

        return progress;
    }

    private static Proto.ExecutionResult ToProtoExecutionResult(AnalysisResultPackage package)
    {
        var proto = new Proto.ExecutionResult
        {
            ResultId = package.ResultPackageId,
            Status = ToProtoJobState(package.Status),
            Summary = package.Summary.Description ?? package.Summary.Title,
            Provenance = ToProtoProvenance(package.Provenance)
        };

        for (var i = 0; i < package.Assumptions.Count; i++)
        {
            proto.Assumptions.Add(new Proto.Assumption
            {
                AssumptionId = $"assumption-{i + 1}",
                Description = package.Assumptions[i]
            });
        }

        foreach (var artifact in package.Artifacts)
        {
            proto.Artifacts.Add(ToProtoArtifactRef(artifact));
        }

        foreach (var error in package.Errors)
        {
            proto.StageResults.Add(new Proto.StageResult
            {
                NodeId = error.StepId ?? "",
                State = Proto.StageState.Failed,
                Error = ToProtoErrorDetail(error)
            });
        }

        return proto;
    }

    private static Proto.ArtifactRef ToProtoArtifactRef(ArtifactRef artifact)
        => new()
        {
            ArtifactId = artifact.ArtifactId,
            ArtifactClass = ToProtoArtifactClass(artifact.Kind),
            ArtifactVersion = 1,
            ProducerRef = artifact.Uri ?? artifact.Label
        };

    private static Proto.ProvenanceRecord ToProtoProvenance(ProvenanceRecord provenance)
    {
        var proto = new Proto.ProvenanceRecord();

        proto.SourceDatasetRefs.AddRange(provenance.Sources.Select(source => source.SourceId));
        proto.ProcessDefinitionRefs.AddRange(provenance.ProcessDefinitions);
        proto.Assumptions.AddRange(provenance.Assumptions.Select((assumption, index) => new Proto.Assumption
        {
            AssumptionId = $"assumption-{index + 1}",
            Description = assumption
        }));

        if (provenance.ExecutedAt.HasValue)
        {
            proto.ExecutedAt = provenance.ExecutedAt.Value.ToUnixTimeMilliseconds();
        }

        return proto;
    }

    private static Proto.ErrorDetail ToProtoErrorDetail(GeoprocessingError error)
        => new()
        {
            ErrorCode = ToProtoErrorCode(error.Kind),
            Category = ToProtoErrorCategory(error.Kind),
            Message = error.Message,
            NodeId = error.StepId ?? "",
            Retryability = ToProtoRetryability(error.Kind)
        };

    private static Proto.PlanValidationIssue ToProtoPlanValidationIssue(
        GeoprocessingValidationFailure failure,
        Proto.IssueSeverity severity)
        => new()
        {
            Field = failure.FieldPath ?? "",
            Message = failure.Message,
            Severity = severity
        };

    // -----------------------------------------------------------------------
    // Enum conversions
    // -----------------------------------------------------------------------

    private static AnalysisPlanStepKind ToDomainPlanStepKind(string kind) => NormalizeToken(kind) switch
    {
        "queryfeatures" => AnalysisPlanStepKind.QueryFeatures,
        "geoprocess" => AnalysisPlanStepKind.Geoprocess,
        "aggregate" => AnalysisPlanStepKind.Aggregate,
        "rendermap" => AnalysisPlanStepKind.RenderMap,
        "export" => AnalysisPlanStepKind.Export,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, $"Unsupported plan step kind: {kind}")
    };

    private static ArtifactKind ToDomainArtifactKind(string kind) => NormalizeToken(kind) switch
    {
        "scalar" => ArtifactKind.Scalar,
        "featurelayer" => ArtifactKind.FeatureLayer,
        "table" => ArtifactKind.Table,
        "raster" => ArtifactKind.Raster,
        "file" => ArtifactKind.File,
        "report" => ArtifactKind.Report,
        "map" => ArtifactKind.Map,
        "appbundle" => ArtifactKind.AppBundle,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, $"Unsupported artifact kind: {kind}")
    };

    private static Proto.ArtifactClass ToProtoArtifactClass(ArtifactKind kind) => kind switch
    {
        ArtifactKind.Scalar => Proto.ArtifactClass.Scalar,
        ArtifactKind.FeatureLayer => Proto.ArtifactClass.FeatureLayer,
        ArtifactKind.Table => Proto.ArtifactClass.Table,
        ArtifactKind.Raster => Proto.ArtifactClass.Raster,
        ArtifactKind.File => Proto.ArtifactClass.File,
        ArtifactKind.Report => Proto.ArtifactClass.Report,
        ArtifactKind.Map => Proto.ArtifactClass.Map,
        ArtifactKind.AppBundle => Proto.ArtifactClass.AppBundle,
        _ => Proto.ArtifactClass.Unspecified
    };

    private static Proto.JobState ToProtoJobState(ExecutionJobStatus status) => status switch
    {
        ExecutionJobStatus.Queued => Proto.JobState.Validated,
        ExecutionJobStatus.Provisioning => Proto.JobState.Running,
        ExecutionJobStatus.Running => Proto.JobState.Running,
        ExecutionJobStatus.Succeeded => Proto.JobState.Completed,
        ExecutionJobStatus.Failed => Proto.JobState.Failed,
        ExecutionJobStatus.Cancelled => Proto.JobState.Cancelled,
        _ => Proto.JobState.Unspecified
    };

    private static Proto.JobState ToProtoJobState(GeoprocessingWorkflowStatus status) => status switch
    {
        GeoprocessingWorkflowStatus.Draft => Proto.JobState.Draft,
        GeoprocessingWorkflowStatus.AwaitingClarification => Proto.JobState.AwaitingClarification,
        GeoprocessingWorkflowStatus.Validated => Proto.JobState.Validated,
        GeoprocessingWorkflowStatus.AwaitingApproval => Proto.JobState.AwaitingApproval,
        GeoprocessingWorkflowStatus.AwaitingExecution => Proto.JobState.Validated,
        GeoprocessingWorkflowStatus.Running => Proto.JobState.Running,
        GeoprocessingWorkflowStatus.Completed => Proto.JobState.Completed,
        GeoprocessingWorkflowStatus.Failed => Proto.JobState.Failed,
        GeoprocessingWorkflowStatus.Cancelled => Proto.JobState.Cancelled,
        _ => Proto.JobState.Unspecified
    };

    private static Proto.ErrorCategory ToProtoErrorCategory(GeoprocessingErrorKind kind) => kind switch
    {
        GeoprocessingErrorKind.ValidationFailed => Proto.ErrorCategory.Validation,
        GeoprocessingErrorKind.AuthorizationDenied => Proto.ErrorCategory.Authorization,
        GeoprocessingErrorKind.UnknownDataset => Proto.ErrorCategory.Validation,
        GeoprocessingErrorKind.UnknownProcess => Proto.ErrorCategory.Validation,
        GeoprocessingErrorKind.ExecutionFailed => Proto.ErrorCategory.Execution,
        GeoprocessingErrorKind.Timeout => Proto.ErrorCategory.Execution,
        GeoprocessingErrorKind.Cancelled => Proto.ErrorCategory.Execution,
        GeoprocessingErrorKind.OutputBindingFailed => Proto.ErrorCategory.Artifact,
        _ => Proto.ErrorCategory.Unspecified
    };

    private static Proto.Retryability ToProtoRetryability(GeoprocessingErrorKind kind) => kind switch
    {
        GeoprocessingErrorKind.ValidationFailed => Proto.Retryability.FixPlanAndRetry,
        GeoprocessingErrorKind.UnknownDataset => Proto.Retryability.FixDataAndRetry,
        GeoprocessingErrorKind.UnknownProcess => Proto.Retryability.FixPlanAndRetry,
        GeoprocessingErrorKind.Timeout => Proto.Retryability.TransientBackendError,
        GeoprocessingErrorKind.OutputBindingFailed => Proto.Retryability.TransientBackendError,
        GeoprocessingErrorKind.ExecutionFailed => Proto.Retryability.TransientBackendError,
        _ => Proto.Retryability.PermanentFailure
    };

    private static string ToProtoErrorCode(GeoprocessingErrorKind kind) => kind switch
    {
        GeoprocessingErrorKind.ValidationFailed => "VALIDATION_FAILED",
        GeoprocessingErrorKind.AuthorizationDenied => "AUTHORIZATION_DENIED",
        GeoprocessingErrorKind.UnknownDataset => "UNKNOWN_DATASET",
        GeoprocessingErrorKind.UnknownProcess => "UNKNOWN_PROCESS",
        GeoprocessingErrorKind.ExecutionFailed => "EXECUTION_FAILED",
        GeoprocessingErrorKind.Timeout => "TIMEOUT",
        GeoprocessingErrorKind.Cancelled => "CANCELLED",
        GeoprocessingErrorKind.OutputBindingFailed => "OUTPUT_BINDING_FAILED",
        _ => "UNSPECIFIED"
    };

    private static string? ResolveProcessId(Proto.PlanStep step)
    {
        foreach (var key in step.Inputs.Keys)
        {
            if (IsProcessIdInput(key))
            {
                return ToDomainInputValue(step.Inputs[key]);
            }
        }

        return null;
    }

    private static bool IsProcessIdInput(string key)
        => string.Equals(key, "processId", StringComparison.Ordinal)
           || string.Equals(key, "process_id", StringComparison.Ordinal);

    private static string ToDomainInputValue(Proto.ParameterValue value)
        => value.KindCase switch
        {
            Proto.ParameterValue.KindOneofCase.StringValue => value.StringValue,
            Proto.ParameterValue.KindOneofCase.Int64Value => value.Int64Value.ToString(CultureInfo.InvariantCulture),
            Proto.ParameterValue.KindOneofCase.DoubleValue => value.DoubleValue.ToString("R", CultureInfo.InvariantCulture),
            Proto.ParameterValue.KindOneofCase.BoolValue => value.BoolValue ? "true" : "false",
            Proto.ParameterValue.KindOneofCase.BytesValue => Convert.ToBase64String(value.BytesValue.ToByteArray()),
            _ => value.ToString()
        };

    public static Proto.ParameterValue ToProtoParameterValue(string value)
        => new() { StringValue = value };

    private static string NormalizeToken(string value)
        => value.Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .Replace(".", "", StringComparison.Ordinal)
            .ToLowerInvariant();
}

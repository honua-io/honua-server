// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Proto = Geospatial.V1;
using DomainPlan = Honua.Core.Features.Geoprocessing.Domain.AnalysisPlan;
using DomainPlanStep = Honua.Core.Features.Geoprocessing.Domain.AnalysisPlanStep;

namespace Honua.Server.Features.Geoprocessing;

/// <summary>
/// Stateless conversion helpers between geoprocessing domain types and proto messages.
/// </summary>
internal static class GeoprocessingConversionHelpers
{
    // -----------------------------------------------------------------------
    // Proto → Domain
    // -----------------------------------------------------------------------

    /// <summary>
    /// Converts a proto AnalysisPlan to the domain representation.
    /// </summary>
    public static DomainPlan ToDomainPlan(Proto.AnalysisPlan proto)
    {
        var steps = proto.Steps.Count == 0
            ? (IReadOnlyList<DomainPlanStep>)[]
            : proto.Steps.Select(ToDomainPlanStep).ToArray();

        var outputs = proto.Outputs.Count == 0
            ? (IReadOnlyList<ArtifactKind>)[]
            : proto.Outputs.Select(ToDomainArtifactKind).ToArray();

        return new DomainPlan
        {
            PlanId = proto.PlanId,
            IntentId = proto.IntentId,
            Steps = steps,
            Outputs = outputs,
            Warnings = proto.Warnings.Count == 0 ? [] : proto.Warnings.ToArray()
        };
    }

    private static DomainPlanStep ToDomainPlanStep(Proto.AnalysisPlanStep proto)
        => new()
        {
            StepId = proto.StepId,
            Kind = ToDomainPlanStepKind(proto.Kind),
            ProcessId = proto.HasProcessId ? proto.ProcessId : null,
            Inputs = proto.Inputs.Count == 0
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(proto.Inputs),
            DependsOn = proto.DependsOn.Count == 0 ? [] : proto.DependsOn.ToArray()
        };

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
            IsExecutable = result.IsExecutable,
            RequiresApproval = result.RequiresApproval
        };

        foreach (var violation in result.Violations)
        {
            response.Violations.Add(ToProtoValidationFailure(violation));
        }

        response.Warnings.AddRange(result.Warnings);
        return response;
    }

    /// <summary>
    /// Converts a domain DryRunResult to the proto response.
    /// </summary>
    public static Proto.DryRunPlanResponse ToProtoDryRunPlanResponse(DryRunResult result)
    {
        var response = new Proto.DryRunPlanResponse
        {
            EstimatedDurationSeconds = result.EstimatedDurationSeconds
        };

        foreach (var artifact in result.EstimatedArtifacts)
        {
            response.EstimatedArtifacts.Add(ToProtoArtifactKind(artifact));
        }

        response.SideEffects.AddRange(result.SideEffects);
        return response;
    }

    /// <summary>
    /// Converts an ExecutionJobRecord to the proto representation.
    /// </summary>
    public static Proto.ExecutionJob ToProtoExecutionJob(ExecutionJobRecord job)
    {
        var proto = new Proto.ExecutionJob
        {
            JobId = job.OperationId,
            Status = ToProtoJobStatus(job.Status),
            CreatedAt = job.CreatedAt.ToUnixTimeMilliseconds(),
            UpdatedAt = job.UpdatedAt.ToUnixTimeMilliseconds()
        };

        if (job.PercentComplete.HasValue)
        {
            proto.PercentComplete = job.PercentComplete.Value;
        }

        if (job.CurrentPhase != null)
        {
            proto.CurrentPhase = job.CurrentPhase;
        }

        if (job.CompletedAt.HasValue)
        {
            proto.CompletedAt = job.CompletedAt.Value.ToUnixTimeMilliseconds();
        }

        if (job.ErrorMessage != null)
        {
            proto.ErrorMessage = job.ErrorMessage;
        }

        proto.Warnings.AddRange(job.Warnings);
        return proto;
    }

    /// <summary>
    /// Converts a domain AnalysisResultPackage to the proto representation.
    /// </summary>
    public static Proto.AnalysisResultPackage ToProtoResultPackage(AnalysisResultPackage package)
    {
        var proto = new Proto.AnalysisResultPackage
        {
            ResultPackageId = package.ResultPackageId,
            Status = ToProtoWorkflowStatus(package.Status),
            Summary = ToProtoResultSummary(package.Summary)
        };

        proto.Assumptions.AddRange(package.Assumptions);

        foreach (var artifact in package.Artifacts)
        {
            proto.Artifacts.Add(ToProtoArtifactRef(artifact));
        }

        foreach (var workspace in package.WorkspaceRefs)
        {
            proto.WorkspaceRefs.Add(ToProtoWorkspaceRef(workspace));
        }

        if (package.MapPackageId != null)
        {
            proto.MapPackageId = package.MapPackageId;
        }

        if (package.AppPackageId != null)
        {
            proto.AppPackageId = package.AppPackageId;
        }

        proto.Provenance = ToProtoProvenance(package.Provenance);

        foreach (var error in package.Errors)
        {
            proto.Errors.Add(ToProtoGeoprocessingError(error));
        }

        return proto;
    }

    // -----------------------------------------------------------------------
    // Individual type conversions
    // -----------------------------------------------------------------------

    private static Proto.ArtifactRef ToProtoArtifactRef(ArtifactRef artifact)
    {
        var proto = new Proto.ArtifactRef
        {
            ArtifactId = artifact.ArtifactId,
            Kind = ToProtoArtifactKind(artifact.Kind),
            Label = artifact.Label
        };

        if (artifact.Uri != null)
        {
            proto.Uri = artifact.Uri;
        }

        if (artifact.ContentType != null)
        {
            proto.ContentType = artifact.ContentType;
        }

        foreach (var (key, value) in artifact.Metadata)
        {
            proto.Metadata[key] = value;
        }

        return proto;
    }

    private static Proto.WorkspaceRef ToProtoWorkspaceRef(WorkspaceRef workspace)
    {
        var proto = new Proto.WorkspaceRef
        {
            WorkspaceId = workspace.WorkspaceId,
            Kind = ToProtoWorkspaceKind(workspace.Kind),
            Label = workspace.Label
        };

        if (workspace.Uri != null)
        {
            proto.Uri = workspace.Uri;
        }

        if (workspace.ExpiresAt.HasValue)
        {
            proto.ExpiresAt = workspace.ExpiresAt.Value.ToUnixTimeMilliseconds();
        }

        return proto;
    }

    private static Proto.ProvenanceRecord ToProtoProvenance(ProvenanceRecord provenance)
    {
        var proto = new Proto.ProvenanceRecord
        {
            ClarificationsAsked = provenance.ClarificationsAsked.Count,
            ClarificationsAnswered = provenance.ClarificationsAnswered.Count
        };

        foreach (var source in provenance.Sources)
        {
            var protoSource = new Proto.ProvenanceSource { SourceId = source.SourceId };
            if (source.Version != null)
            {
                protoSource.Version = source.Version;
            }

            if (source.Description != null)
            {
                protoSource.Description = source.Description;
            }

            proto.Sources.Add(protoSource);
        }

        proto.ProcessDefinitions.AddRange(provenance.ProcessDefinitions);
        proto.Assumptions.AddRange(provenance.Assumptions);

        if (provenance.ExecutedAt.HasValue)
        {
            proto.ExecutedAt = provenance.ExecutedAt.Value.ToUnixTimeMilliseconds();
        }

        proto.GeneratedArtifactIds.AddRange(provenance.GeneratedArtifactIds);
        return proto;
    }

    private static Proto.ResultSummary ToProtoResultSummary(ResultSummary summary)
    {
        var proto = new Proto.ResultSummary { Title = summary.Title };
        if (summary.Description != null)
        {
            proto.Description = summary.Description;
        }

        return proto;
    }

    private static Proto.GeoprocessingError ToProtoGeoprocessingError(GeoprocessingError error)
    {
        var proto = new Proto.GeoprocessingError
        {
            Kind = ToProtoErrorKind(error.Kind),
            Message = error.Message
        };

        if (error.StepId != null)
        {
            proto.StepId = error.StepId;
        }

        if (error.Violations != null)
        {
            foreach (var violation in error.Violations)
            {
                proto.Violations.Add(ToProtoValidationFailure(violation));
            }
        }

        return proto;
    }

    private static Proto.ValidationFailure ToProtoValidationFailure(GeoprocessingValidationFailure failure)
    {
        var proto = new Proto.ValidationFailure
        {
            Code = failure.Code,
            Message = failure.Message
        };

        if (failure.FieldPath != null)
        {
            proto.FieldPath = failure.FieldPath;
        }

        return proto;
    }

    // -----------------------------------------------------------------------
    // Enum conversions
    // -----------------------------------------------------------------------

    private static AnalysisPlanStepKind ToDomainPlanStepKind(Proto.PlanStepKind kind) => kind switch
    {
        Proto.PlanStepKind.QueryFeatures => AnalysisPlanStepKind.QueryFeatures,
        Proto.PlanStepKind.Geoprocess => AnalysisPlanStepKind.Geoprocess,
        Proto.PlanStepKind.Aggregate => AnalysisPlanStepKind.Aggregate,
        Proto.PlanStepKind.RenderMap => AnalysisPlanStepKind.RenderMap,
        Proto.PlanStepKind.Export => AnalysisPlanStepKind.Export,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, $"Unsupported plan step kind: {kind}")
    };

    private static ArtifactKind ToDomainArtifactKind(Proto.ArtifactKind kind) => kind switch
    {
        Proto.ArtifactKind.Scalar => ArtifactKind.Scalar,
        Proto.ArtifactKind.FeatureLayer => ArtifactKind.FeatureLayer,
        Proto.ArtifactKind.Table => ArtifactKind.Table,
        Proto.ArtifactKind.Raster => ArtifactKind.Raster,
        Proto.ArtifactKind.File => ArtifactKind.File,
        Proto.ArtifactKind.Report => ArtifactKind.Report,
        Proto.ArtifactKind.Map => ArtifactKind.Map,
        Proto.ArtifactKind.AppBundle => ArtifactKind.AppBundle,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, $"Unsupported artifact kind: {kind}")
    };

    private static Proto.ArtifactKind ToProtoArtifactKind(ArtifactKind kind) => kind switch
    {
        ArtifactKind.Scalar => Proto.ArtifactKind.Scalar,
        ArtifactKind.FeatureLayer => Proto.ArtifactKind.FeatureLayer,
        ArtifactKind.Table => Proto.ArtifactKind.Table,
        ArtifactKind.Raster => Proto.ArtifactKind.Raster,
        ArtifactKind.File => Proto.ArtifactKind.File,
        ArtifactKind.Report => Proto.ArtifactKind.Report,
        ArtifactKind.Map => Proto.ArtifactKind.Map,
        ArtifactKind.AppBundle => Proto.ArtifactKind.AppBundle,
        _ => Proto.ArtifactKind.Unspecified
    };

    private static Proto.WorkspaceKind ToProtoWorkspaceKind(WorkspaceKind kind) => kind switch
    {
        WorkspaceKind.Scratch => Proto.WorkspaceKind.Scratch,
        WorkspaceKind.Persistent => Proto.WorkspaceKind.Persistent,
        WorkspaceKind.TempLayer => Proto.WorkspaceKind.TempLayer,
        WorkspaceKind.SavedLayer => Proto.WorkspaceKind.SavedLayer,
        WorkspaceKind.ResultCollection => Proto.WorkspaceKind.ResultCollection,
        _ => Proto.WorkspaceKind.Unspecified
    };

    private static Proto.WorkflowStatus ToProtoWorkflowStatus(GeoprocessingWorkflowStatus status) => status switch
    {
        GeoprocessingWorkflowStatus.Draft => Proto.WorkflowStatus.Draft,
        GeoprocessingWorkflowStatus.AwaitingClarification => Proto.WorkflowStatus.AwaitingClarification,
        GeoprocessingWorkflowStatus.Validated => Proto.WorkflowStatus.Validated,
        GeoprocessingWorkflowStatus.AwaitingApproval => Proto.WorkflowStatus.AwaitingApproval,
        GeoprocessingWorkflowStatus.AwaitingExecution => Proto.WorkflowStatus.AwaitingExecution,
        GeoprocessingWorkflowStatus.Running => Proto.WorkflowStatus.Running,
        GeoprocessingWorkflowStatus.Completed => Proto.WorkflowStatus.Completed,
        GeoprocessingWorkflowStatus.Failed => Proto.WorkflowStatus.Failed,
        GeoprocessingWorkflowStatus.Cancelled => Proto.WorkflowStatus.Cancelled,
        _ => Proto.WorkflowStatus.Unspecified
    };

    private static Proto.JobStatus ToProtoJobStatus(ExecutionJobStatus status) => status switch
    {
        ExecutionJobStatus.Queued => Proto.JobStatus.Queued,
        ExecutionJobStatus.Provisioning => Proto.JobStatus.Provisioning,
        ExecutionJobStatus.Running => Proto.JobStatus.Running,
        ExecutionJobStatus.Succeeded => Proto.JobStatus.Succeeded,
        ExecutionJobStatus.Failed => Proto.JobStatus.Failed,
        ExecutionJobStatus.Cancelled => Proto.JobStatus.Cancelled,
        _ => Proto.JobStatus.Unspecified
    };

    private static Proto.ErrorKind ToProtoErrorKind(GeoprocessingErrorKind kind) => kind switch
    {
        GeoprocessingErrorKind.ValidationFailed => Proto.ErrorKind.ValidationFailed,
        GeoprocessingErrorKind.AuthorizationDenied => Proto.ErrorKind.AuthorizationDenied,
        GeoprocessingErrorKind.UnknownDataset => Proto.ErrorKind.UnknownDataset,
        GeoprocessingErrorKind.UnknownProcess => Proto.ErrorKind.UnknownProcess,
        GeoprocessingErrorKind.ExecutionFailed => Proto.ErrorKind.ExecutionFailed,
        GeoprocessingErrorKind.Timeout => Proto.ErrorKind.Timeout,
        GeoprocessingErrorKind.Cancelled => Proto.ErrorKind.Cancelled,
        GeoprocessingErrorKind.OutputBindingFailed => Proto.ErrorKind.OutputBindingFailed,
        _ => Proto.ErrorKind.Unspecified
    };
}

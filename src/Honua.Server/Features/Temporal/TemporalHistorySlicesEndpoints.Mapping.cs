// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Temporal.Domain;

namespace Honua.Server.Features.Temporal;

/// <summary>
/// Domain-to-DTO mapping for the temporal history slice 2-5 endpoints (honua-server#1166).
/// </summary>
internal static partial class TemporalHistorySlicesEndpoints
{
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

    private static TemporalDiffResponse ToResponse(TemporalDiffResult result)
        => new()
        {
            ServiceId = result.ServiceId,
            LayerId = result.LayerId,
            From = ToResponse(result.FromCheckpoint),
            To = ToResponse(result.ToCheckpoint),
            Summary = new TemporalDiffSummaryResponse
            {
                Added = result.Summary.Added,
                Removed = result.Summary.Removed,
                AttributeChanged = result.Summary.AttributeChanged,
                GeometryChanged = result.Summary.GeometryChanged,
                Total = result.Summary.Total
            },
            Changes = result.Changes
                .Select(static change => new TemporalFeatureDiffResponse
                {
                    ObjectId = change.ObjectId,
                    PrimaryClass = change.PrimaryClass.ToString(),
                    Classes = change.Classes.Select(static c => c.ToString()).ToArray(),
                    GeometryChanged = change.GeometryChanged,
                    FieldChanges = change.FieldChanges
                        .Select(static field => new TemporalFieldChangeResponse
                        {
                            Field = field.Field,
                            OldValue = field.OldValue,
                            NewValue = field.NewValue,
                            Masked = field.Masked
                        })
                        .ToArray(),
                    Attribution = ToResponse(change.Attribution)
                })
                .ToArray(),
            NextCursor = result.NextCursor
        };

    private static TemporalTimelineResponse ToResponse(TemporalTimeline timeline)
        => new()
        {
            ServiceId = timeline.ServiceId,
            LayerId = timeline.LayerId,
            ObjectId = timeline.ObjectId,
            CurrentGeneration = timeline.CurrentGeneration,
            Revisions = timeline.Revisions
                .Select(static revision => new TemporalRevisionResponse
                {
                    Generation = revision.Generation,
                    Operation = revision.Operation.ToString(),
                    ChangedAt = revision.ChangedAtUtc.ToString(TimestampFormat, CultureInfo.InvariantCulture),
                    Attribution = ToResponse(revision.Attribution)
                })
                .ToArray(),
            NextCursor = timeline.NextCursor
        };

    private static TemporalRollbackPlanResponse ToResponse(TemporalRollbackPlan plan)
        => new()
        {
            ServiceId = plan.ServiceId,
            LayerId = plan.LayerId,
            TargetCheckpoint = ToResponse(plan.TargetCheckpoint),
            CurrentGeneration = plan.CurrentGeneration,
            State = plan.State.ToString(),
            AffectedFeatureCount = plan.AffectedFeatureCount,
            ValidationFindings = plan.ValidationFindings.Select(ToResponse).ToArray(),
            CompatibilityFindings = plan.CompatibilityFindings.Select(ToResponse).ToArray(),
            RequiresApproval = plan.RequiresApproval
        };

    private static TemporalRollbackJobResponse ToResponse(TemporalRollbackJobHandle handle)
        => new()
        {
            JobId = handle.JobId,
            ServiceId = handle.ServiceId,
            LayerId = handle.LayerId,
            TargetCheckpoint = ToResponse(handle.TargetCheckpoint),
            Status = handle.Status
        };

    private static TemporalCheckpointResponse ToResponse(ResolvedTemporalCheckpoint checkpoint)
        => new()
        {
            Kind = checkpoint.Requested.Kind.ToString(),
            Value = checkpoint.Requested.Value,
            Generation = checkpoint.Generation
        };

    private static TemporalRollbackFindingResponse ToResponse(TemporalRollbackFinding finding)
        => new()
        {
            Code = finding.Code,
            Severity = finding.Severity.ToString(),
            Message = finding.Message
        };

    private static TemporalAttributionResponse? ToResponse(TemporalAttribution? attribution)
        => attribution is null
            ? null
            : new TemporalAttributionResponse
            {
                Actor = attribution.Actor,
                Source = attribution.Source.ToString(),
                Operation = attribution.Operation,
                SourceId = attribution.SourceId
            };
}

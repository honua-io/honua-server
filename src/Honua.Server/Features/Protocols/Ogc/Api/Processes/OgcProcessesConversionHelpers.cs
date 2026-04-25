// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.Server.Features.Protocols.Ogc.Api.Processes.Models;

namespace Honua.Server.Features.Protocols.Ogc.Api.Processes;

/// <summary>
/// Stateless conversion helpers between canonical domain types and OGC API Processes models.
/// Maps ExecutionJobStatus to OGC status strings per the lifecycle state matrix defined in
/// the geoprocess framework analysis (honua-server#360).
/// </summary>
internal static class OgcProcessesConversionHelpers
{
    /// <summary>
    /// Maps a canonical <see cref="ExecutionJobStatus"/> to an OGC API Processes status string.
    /// </summary>
    /// <remarks>
    /// State matrix (from geoprocess-framework-analysis.md):
    /// Queued → accepted, Provisioning → accepted, Running → running,
    /// Succeeded → successful, Failed → failed, Cancelled → dismissed.
    /// </remarks>
    public static string ToOgcStatus(ExecutionJobStatus status) => status switch
    {
        ExecutionJobStatus.Queued => "accepted",
        ExecutionJobStatus.Provisioning => "accepted",
        ExecutionJobStatus.Running => "running",
        ExecutionJobStatus.Succeeded => "successful",
        ExecutionJobStatus.Failed => "failed",
        ExecutionJobStatus.Cancelled => "dismissed",
        _ => "accepted"
    };

    /// <summary>
    /// Converts an <see cref="ExecutionJobRecord"/> to an OGC <see cref="OgcStatusInfo"/>.
    /// </summary>
    public static OgcStatusInfo ToOgcStatusInfo(
        ExecutionJobRecord job,
        string? processId,
        string baseUrl)
    {
        var jobUrl = $"{baseUrl}/ogc/processes/jobs/{job.OperationId}";
        var linksBuilder = ImmutableArray.CreateBuilder<Link>();
        linksBuilder.Add(Link.Create(jobUrl, RelationTypes.Self, MediaTypes.Json, "Job status"));

        var ogcStatus = ToOgcStatus(job.Status);

        // Per OGC API Processes Part 1 §7.11.1, succeeded jobs expose a results
        // resource. Only Succeeded resolves to a usable results document today —
        // Failed returns 500 and Cancelled returns 410, so those states keep the
        // link suppressed to avoid misdirecting clients.
        if (job.Status == ExecutionJobStatus.Succeeded)
        {
            linksBuilder.Add(Link.Create(
                $"{jobUrl}/results",
                RelationTypes.OgcResults,
                MediaTypes.Json,
                "Job results"));
        }

        return new OgcStatusInfo
        {
            ProcessId = processId,
            JobId = job.OperationId,
            Status = ogcStatus,
            Message = job.ErrorMessage ?? job.CurrentPhase,
            Created = job.CreatedAt.ToString("o", CultureInfo.InvariantCulture),
            Updated = job.UpdatedAt.ToString("o", CultureInfo.InvariantCulture),
            Progress = job.PercentComplete.HasValue
                ? (int)Math.Clamp(job.PercentComplete.Value, 0, 100)
                : null,
            Links = linksBuilder.ToImmutable()
        };
    }

    /// <summary>
    /// Converts a cancelled execution job into the OGC API Processes dismissed status shape.
    /// </summary>
    public static OgcStatusInfo ToOgcDismissedStatusInfo(
        ExecutionJobRecord job,
        string? processId,
        string baseUrl)
    {
        var statusInfo = ToOgcStatusInfo(job, processId, baseUrl);
        return statusInfo with
        {
            Status = "dismissed",
            Message = "Job dismissed"
        };
    }

    /// <summary>
    /// Returns true if the job status is a terminal state.
    /// </summary>
    public static bool IsTerminal(ExecutionJobStatus status)
        => status is ExecutionJobStatus.Succeeded
            or ExecutionJobStatus.Failed
            or ExecutionJobStatus.Cancelled;
}

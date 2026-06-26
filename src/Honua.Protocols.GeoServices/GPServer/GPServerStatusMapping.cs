// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Protocols.GeoServices.GPServer;

/// <summary>
/// Maps canonical <see cref="ExecutionJobStatus"/> to Esri GPServer job status strings
/// per ADR-0029 lifecycle state mapping.
/// </summary>
internal static class GPServerStatusMapping
{
    /// <summary>
    /// Translates a canonical execution job status to the Esri GPServer string.
    /// </summary>
    public static string ToEsriJobStatus(ExecutionJobStatus status) => status switch
    {
        ExecutionJobStatus.Queued => "esriJobSubmitted",
        ExecutionJobStatus.Provisioning => "esriJobWaiting",
        ExecutionJobStatus.Running => "esriJobExecuting",
        ExecutionJobStatus.Succeeded => "esriJobSucceeded",
        ExecutionJobStatus.Failed => "esriJobFailed",
        ExecutionJobStatus.Cancelled => "esriJobCancelled",
        _ => "esriJobSubmitted"
    };

    /// <summary>
    /// Returns true if the Esri status represents a terminal state.
    /// </summary>
    public static bool IsTerminalEsriStatus(string esriStatus)
        => esriStatus is "esriJobSucceeded" or "esriJobFailed" or "esriJobCancelled";

    /// <summary>
    /// Parses an Esri GPServer job status string back to a canonical
    /// <see cref="ExecutionJobStatus"/> for jobs-listing status filters. Comparison is
    /// case-insensitive. Returns false for unrecognised values.
    /// </summary>
    /// <param name="esriStatus">The Esri job status string.</param>
    /// <param name="status">The parsed canonical status when recognised.</param>
    /// <returns><see langword="true"/> when the value maps to a known status.</returns>
    public static bool TryParseEsriJobStatus(string esriStatus, out ExecutionJobStatus status)
    {
        switch (esriStatus?.Trim().ToLowerInvariant())
        {
            case "esrijobsubmitted":
                status = ExecutionJobStatus.Queued;
                return true;
            case "esrijobwaiting":
                status = ExecutionJobStatus.Provisioning;
                return true;
            case "esrijobexecuting":
                status = ExecutionJobStatus.Running;
                return true;
            case "esrijobsucceeded":
                status = ExecutionJobStatus.Succeeded;
                return true;
            case "esrijobfailed":
                status = ExecutionJobStatus.Failed;
                return true;
            case "esrijobcancelled":
                status = ExecutionJobStatus.Cancelled;
                return true;
            default:
                status = default;
                return false;
        }
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Protocols.GeoServices.GPServer;

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
}

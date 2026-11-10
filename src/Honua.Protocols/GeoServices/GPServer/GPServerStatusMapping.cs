// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Protocols.GeoServices.GPServer;

internal static class GPServerStatusMapping
{
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

    public static bool IsTerminalEsriStatus(string esriStatus)
        => esriStatus is "esriJobSucceeded" or "esriJobFailed" or "esriJobCancelled";

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

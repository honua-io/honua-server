// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Honua.Protocols.Ogc.Common;
using Honua.Protocols.Ogc.Api.Processes.Models;
using Honua.ServiceDefaults;

namespace Honua.Protocols.Ogc.Api.Processes;

internal static class OgcProcessesResults
{
    public static IResult Error(
        int statusCode,
        string title,
        string detail,
        string type = "about:blank")
        => Results.Json(
            new OgcProcessError
            {
                Type = type,
                Title = title,
                Status = statusCode,
                Detail = detail
            },
            OgcProcessesJsonContext.Default.OgcProcessError,
            MediaTypes.Json,
            statusCode);

    public static IResult NoSuchProcess(string processId)
        => Error(
            StatusCodes.Status404NotFound,
            "No such process",
            $"Process '{processId}' does not exist.",
            "http://www.opengis.net/def/exceptions/ogcapi-processes-1/1.0/no-such-process");

    public static IResult NoSuchJob(string jobId)
        => Error(
            StatusCodes.Status404NotFound,
            "No such job",
            $"Job '{jobId}' does not exist.",
            "http://www.opengis.net/def/exceptions/ogcapi-processes-1/1.0/no-such-job");

    public static IResult StoreUnavailable(GeoprocessingStoreUnavailableException? exception = null)
        => Results.Json(
            new OgcProcessError
            {
                Type = CapabilityUnavailableCodes.ProblemType,
                Title = CapabilityUnavailableCodes.Title,
                Status = StatusCodes.Status503ServiceUnavailable,
                Detail = exception?.HasDependencyReceipt == true
                    ? exception.Message
                    : CapabilityUnavailableCodes.DurableJobStoreDetail,
                Code = exception?.ErrorCode ?? CapabilityUnavailableCodes.ErrorCode,
                Capability = exception?.CapabilityId ?? CapabilityUnavailableCodes.DurableJobsCapability,
                MissingDependency = exception?.HasDependencyReceipt == true
                    ? exception.MissingDependency
                    : CapabilityUnavailableCodes.SystemDatabaseDependency,
                Remediation = exception?.Remediation ?? CapabilityUnavailableCodes.SystemDatabaseRemediation,
                RemediationRef = exception?.RemediationRef ?? CapabilityUnavailableCodes.SystemDatabaseRemediationRef,
            },
            OgcProcessesJsonContext.Default.OgcProcessError,
            MediaTypes.Json,
            StatusCodes.Status503ServiceUnavailable);

    public static IResult Dismissed(
        ExecutionJobRecord job,
        string? processId,
        string baseUrl)
        => Results.Json(
            OgcProcessesConversionHelpers.ToOgcDismissedStatusInfo(job, processId, baseUrl),
            OgcProcessesJsonContext.Default.OgcStatusInfo,
            MediaTypes.Json,
            StatusCodes.Status200OK);

    public static void RecordException(Exception ex)
    {
        HonuaTelemetry.RecordException(Activity.Current, ex);
    }
}

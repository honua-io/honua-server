// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

    /// <summary>
    /// The canonical typed refusal for a job operation attempted on a server whose durable job
    /// store was never composed (honua-release#202). Redis is optional for a local install, so
    /// this path must be machine-readable rather than a bare 503: the payload names the missing
    /// dependency, the capability id it disables, and the remediation. It is emitted up front —
    /// a job is never accepted and then left un-drainable.
    /// </summary>
    /// <param name="exception">
    /// The originating store-unavailable exception, when the caller has one. Adapters that reuse
    /// the exception for a different missing dependency carry no receipt, so the generic
    /// durable-job-store receipt is used only when the exception omits one.
    /// </param>
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
                Code = CapabilityUnavailableCodes.ErrorCode,
                Capability = exception?.CapabilityId ?? CapabilityUnavailableCodes.DurableJobsCapability,
                MissingDependency = exception?.MissingDependency ?? CapabilityUnavailableCodes.RedisDependency,
                Remediation = exception?.Remediation ?? CapabilityUnavailableCodes.RedisRemediation,
                RemediationRef = exception?.RemediationRef ?? CapabilityUnavailableCodes.RedisRemediationRef,
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

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.Server.Features.Protocols.Ogc.Api.Processes.Models;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.Protocols.Ogc.Api.Processes;

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

    public static IResult StoreUnavailable()
        => Error(
            StatusCodes.Status503ServiceUnavailable,
            "Service unavailable",
            "Job operations require Redis-backed durable storage.");

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

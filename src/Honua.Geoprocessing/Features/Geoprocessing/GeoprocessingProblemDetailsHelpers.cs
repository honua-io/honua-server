// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Infrastructure.Models;

namespace Honua.Geoprocessing;

internal static class GeoprocessingProblemDetailsHelpers
{
    public static IResult StoreUnavailable(
        HttpContext context,
        GeoprocessingStoreUnavailableException exception)
        => exception.HasDependencyReceipt
            ? ProblemDetailsHelpers.CreateCapabilityUnavailableProblem(
                context,
                exception.Message,
                exception.MissingDependency,
                exception.Remediation!,
                exception.RemediationRef!,
                exception.CapabilityId,
                exception.ErrorCode,
                exception.MissingEntitlement)
            : ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status503ServiceUnavailable,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status503ServiceUnavailable),
                exception.Message);

    public static IResult IdempotencyConflict(
        HttpContext context,
        GeoprocessingIdempotencyConflictException exception)
    {
        var details = ProblemDetailsHelpers.CreateAdminProblemDetails(
            context,
            StatusCodes.Status409Conflict,
            exception.Message) with
        {
            Code = "idempotency_conflict",
            ConflictingJobId = exception.ConflictingJobId,
        };
        return Json(context, details, StatusCodes.Status409Conflict);
    }

    public static IResult Admission(
        HttpContext context,
        GeoprocessingAdmissionException exception)
    {
        var throttled = exception.Outcome == ExecutionAdmissionOutcome.Throttled;
        var status = throttled
            ? StatusCodes.Status429TooManyRequests
            : StatusCodes.Status503ServiceUnavailable;
        var details = ProblemDetailsHelpers.CreateAdminProblemDetails(
            context,
            status,
            exception.Message) with
        {
            Code = throttled ? "admission_throttled" : "admission_denied",
            PolicyRef = exception.PolicyRef,
            Retryable = exception.RetryAfterSeconds > 0,
            RetryAfterSeconds = exception.RetryAfterSeconds,
        };
        return Json(context, details, status);
    }

    public static IResult ApprovalRequired(
        HttpContext context,
        GeoprocessingApprovalRequiredException exception)
    {
        var details = ProblemDetailsHelpers.CreateAdminProblemDetails(
            context,
            StatusCodes.Status403Forbidden,
            exception.Message) with
        {
            Code = "approval_required",
            PolicyRef = exception.PolicyRef,
            ProposalId = exception.ProposalId,
        };
        return Json(context, details, StatusCodes.Status403Forbidden);
    }

    private static IResult Json(HttpContext context, ProblemDetailsResponse details, int statusCode)
    {
        if (details.RetryAfterSeconds is int retryAfter && retryAfter > 0)
        {
            context.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        }

        return Results.Json(
            details,
            ProblemJsonContext.Default.ProblemDetailsResponse,
            statusCode: statusCode,
            contentType: ProblemDetailsHelpers.ContentType);
    }
}

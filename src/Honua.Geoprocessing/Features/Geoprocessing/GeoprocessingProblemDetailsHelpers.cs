// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

    /// <summary>
    /// Maps an idempotency-key collision to 409 and includes the winning job id.
    /// </summary>
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

    /// <summary>
    /// Maps admission control to 429 when the outcome is throttled and 503 otherwise,
    /// with <c>Retry-After</c> taken from <see cref="GeoprocessingAdmissionException.RetryAfterSeconds"/>.
    /// </summary>
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

    /// <summary>
    /// Maps an approval gate to 403 and includes the policy reference and proposal id.
    /// </summary>
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

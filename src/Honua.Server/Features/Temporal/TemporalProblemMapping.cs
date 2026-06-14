// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Temporal.Abstractions;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Temporal;

/// <summary>
/// Shared exception-to-problem mapping for the temporal history endpoints (honua-server#1166). Maps
/// temporal domain exceptions to RFC 7807 problem responses without leaking provider internals, used by
/// both the slice-1 and slice 2-5 endpoint groups so error mapping stays consistent.
/// </summary>
internal static class TemporalProblemMapping
{
    /// <summary>
    /// Attempts to map a temporal domain exception to a problem result.
    /// </summary>
    /// <param name="ex">The exception to map.</param>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="problem">The mapped problem result when the exception is a known temporal failure.</param>
    /// <returns>True when the exception was mapped; false to let the caller emit a generic 500.</returns>
    public static bool TryMap(Exception ex, HttpContext context, out IResult problem)
    {
        switch (ex)
        {
            case TemporalValidationException validationEx:
                problem = ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status400BadRequest,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                    validationEx.Message);
                return true;
            case TemporalLayerNotFoundException notFoundEx:
                problem = ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status404NotFound,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                    notFoundEx.Message);
                return true;
            case TemporalNotSupportedException notSupportedEx:
                problem = ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status409Conflict,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status409Conflict),
                    notSupportedEx.Message);
                return true;
            case TemporalRollbackNotPermittedException notPermittedEx:
                problem = ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status409Conflict,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status409Conflict),
                    notPermittedEx.Message);
                return true;
            default:
                problem = Results.Empty;
                return false;
        }
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Temporal.Abstractions;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Temporal;

// The problem mapping does not include provider internals.
internal static class TemporalProblemMapping
{
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

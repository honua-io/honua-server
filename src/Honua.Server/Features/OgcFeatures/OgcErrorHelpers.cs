// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.OgcFeatures;

/// <summary>
/// Helper methods for creating OGC API Features error responses.
/// </summary>
internal static class OgcErrorHelpers
{
    public static IResult CreateBadRequest(HttpContext context, string detail)
        => ProblemDetailsHelpers.CreateOgcProblem(context, StatusCodes.Status400BadRequest, detail);

    public static IResult CreateNotFound(HttpContext context, string detail)
        => ProblemDetailsHelpers.CreateOgcProblem(context, StatusCodes.Status404NotFound, detail);

    public static IResult CreateConflict(HttpContext context, string detail)
        => ProblemDetailsHelpers.CreateOgcProblem(context, StatusCodes.Status409Conflict, detail);

    public static IResult CreateInternalServerError(HttpContext context, string detail)
        => ProblemDetailsHelpers.CreateOgcProblem(context, StatusCodes.Status500InternalServerError, detail);
}

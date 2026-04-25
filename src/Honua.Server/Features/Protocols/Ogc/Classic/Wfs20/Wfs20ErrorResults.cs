// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Protocols.Ogc.Classic.Wfs20;

internal static class Wfs20ErrorResults
{
    internal static IResult CreateBadRequest(
        HttpContext context,
        string exceptionCode,
        string detail,
        string? locator = null,
        IReadOnlyList<string>? additionalDetails = null)
    {
        return Create(
            context,
            StandardErrorResponse.BadRequest(detail, additionalDetails),
            exceptionCode,
            locator);
    }

    internal static IResult CreateNotImplemented(
        HttpContext context,
        string exceptionCode,
        string detail,
        string? locator = null,
        IReadOnlyList<string>? additionalDetails = null)
    {
        return Create(
            context,
            new StandardErrorResponse(
                StatusCodes.Status501NotImplemented,
                "Not Implemented",
                detail,
                additionalDetails),
            exceptionCode,
            locator);
    }

    internal static IResult CreateNotFound(
        HttpContext context,
        string exceptionCode,
        string detail,
        string? locator = null,
        IReadOnlyList<string>? additionalDetails = null)
    {
        return Create(
            context,
            StandardErrorResponse.NotFound(detail, additionalDetails),
            exceptionCode,
            locator);
    }

    private static IResult Create(
        HttpContext context,
        StandardErrorResponse errorResponse,
        string exceptionCode,
        string? locator)
    {
        return StandardErrorResponseFormatter.FormatError(
            context,
            errorResponse,
            new ErrorResponseFormatterOptions
            {
                WfsExceptionCode = exceptionCode,
                WfsExceptionLocator = locator
            });
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Validation;

namespace Honua.Server.Features.OData.Services;

internal sealed record ODataPagingParameters(
    int? Top,
    int? Skip,
    bool? Count,
    PaginationValues Pagination);

internal static class ODataRequestValidation
{
    public static IResult? ValidateAllowedParameters(
        HttpContext context,
        ODataValidationService validationService,
        IReadOnlySet<string> allowedParameters)
    {
        var validationResult = validationService.ValidateAllowedParameters(context.Request.Query.Keys.ToArray(), allowedParameters);
        if (!validationResult.IsValid)
        {
            return ODataUtilityService.CreateODataError(
                context,
                "InvalidQueryOption",
                validationResult.ErrorMessage ?? "Invalid query parameter.");
        }

        return null;
    }

    public static IResult? ValidateFormat(
        HttpContext context,
        ODataValidationService validationService,
        string? format)
    {
        var validation = validationService.ValidateFormat(format, ODataUtilityService.GetAllowedFormats());
        if (!validation.IsValid)
        {
            return ODataUtilityService.CreateODataError(
                context,
                "InvalidQueryOption",
                validation.ErrorMessage ?? "Invalid format parameter.");
        }

        return null;
    }

    public static bool TryParsePaging(
        HttpContext context,
        ODataValidationService validationService,
        string? top,
        string? skip,
        string? count,
        out ODataPagingParameters? paging,
        out IResult? error)
    {
        paging = null;
        error = null;

        if (!ODataParsingUtilities.TryParseOptionalInt(top, "$top", out var topValue, out var parseError))
        {
            error = ODataUtilityService.CreateODataError(context, "InvalidQueryOption", parseError!);
            return false;
        }

        if (!ODataParsingUtilities.TryParseOptionalInt(skip, "$skip", out var skipValue, out parseError))
        {
            error = ODataUtilityService.CreateODataError(context, "InvalidQueryOption", parseError!);
            return false;
        }

        if (!ODataParsingUtilities.TryParseOptionalBool(count, "$count", out var countValue, out parseError))
        {
            error = ODataUtilityService.CreateODataError(context, "InvalidQueryOption", parseError!);
            return false;
        }

        var paginationResult = validationService.ValidateAndNormalizePagination(skipValue, topValue);
        if (!paginationResult.IsValid)
        {
            error = ODataUtilityService.CreateODataError(
                context,
                "InvalidQueryOption",
                paginationResult.ErrorMessage ?? "Invalid OData query.");
            return false;
        }

        paging = new ODataPagingParameters(topValue, skipValue, countValue, paginationResult.Value!);
        return true;
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Validation;
using Honua.Server.Features.Infrastructure.Validation;

namespace Honua.Server.Features.OData.Services;

internal sealed record ODataPagingParameters(
    int? Top,
    int? Skip,
    bool? Count,
    PaginationValues Pagination,
    bool UseSkipToken);

internal static class ODataRequestValidation
{
    public static IResult? ValidateAllowedParameters(
        HttpContext context,
        ODataValidationService validationService,
        IReadOnlySet<string> allowedParameters)
    {
        var validationResult = validationService.ValidateAllowedParameters(context.Request.Query.Keys.ToArray(), allowedParameters);
        var error = QueryParameterValidationHelpers.GetValidationError(validationResult);
        return error == null
            ? null
            : ODataUtilityService.CreateODataError(context, "InvalidQueryOption", error);
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
        string? skiptoken,
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

        if (!ODataParsingUtilities.TryParseOptionalInt(skiptoken, "$skiptoken", out var skipTokenValue, out parseError))
        {
            error = ODataUtilityService.CreateODataError(context, "InvalidQueryOption", parseError!);
            return false;
        }

        if (skipValue.HasValue && skipTokenValue.HasValue)
        {
            error = ODataUtilityService.CreateODataError(
                context,
                "InvalidQueryOption",
                "$skip and $skiptoken cannot be used together.");
            return false;
        }

        if (!ODataParsingUtilities.TryParseOptionalBool(count, "$count", out var countValue, out parseError))
        {
            error = ODataUtilityService.CreateODataError(context, "InvalidQueryOption", parseError!);
            return false;
        }

        var resolvedSkip = skipTokenValue ?? skipValue;
        var paginationResult = validationService.ValidateAndNormalizePagination(resolvedSkip, topValue);
        if (!paginationResult.IsValid)
        {
            error = ODataUtilityService.CreateODataError(
                context,
                "InvalidQueryOption",
                paginationResult.ErrorMessage ?? "Invalid OData query.");
            return false;
        }

        paging = new ODataPagingParameters(
            topValue,
            resolvedSkip,
            countValue,
            paginationResult.Value!,
            skipTokenValue.HasValue && !skipValue.HasValue);
        return true;
    }

    public static IResult? TryParsePagingOrError(
        HttpContext context,
        ODataValidationService validationService,
        string? top,
        string? skip,
        string? skiptoken,
        string? count,
        out ODataPagingParameters? paging)
    {
        if (!TryParsePaging(context, validationService, top, skip, skiptoken, count, out paging, out var error))
        {
            return error;
        }

        return null;
    }

    public static IResult? TryGetPagingValues(
        HttpContext context,
        ODataValidationService validationService,
        string? top,
        string? skip,
        string? skiptoken,
        string? count,
        out PaginationValues pagination,
        out int? topValue,
        out int? skipValue,
        out bool? countValue,
        out bool useSkipToken)
    {
        var error = TryParsePagingOrError(context, validationService, top, skip, skiptoken, count, out var paging);
        if (error != null)
        {
            pagination = new PaginationValues(0, 0);
            topValue = null;
            skipValue = null;
            countValue = null;
            useSkipToken = false;
            return error;
        }

        pagination = paging!.Pagination;
        topValue = paging.Top;
        skipValue = paging.Skip;
        countValue = paging.Count;
        useSkipToken = paging.UseSkipToken;
        return null;
    }
}

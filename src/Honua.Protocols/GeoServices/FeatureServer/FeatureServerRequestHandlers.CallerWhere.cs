// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Models;

namespace Honua.Protocols.GeoServices.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    // The caller's where clause, translated without the layer's hidden fields, so a hidden field is
    // refused exactly like an undeclared one. A blank where yields no filter.
    private static (SqlFragment? SqlFilter, IResult? Error) TranslateCallerWhere(
        HttpContext context,
        MetadataV2Object resource,
        string? where)
    {
        if (string.IsNullOrWhiteSpace(where))
        {
            return (null, null);
        }

        var filterService = context.RequestServices.GetRequiredService<IFilterExpressionService>();
        var parseResult = filterService.Parse(FilterLanguage.GeoServicesSql, where);
        if (!parseResult.IsSuccess)
        {
            return (null, InvalidFilter(context, parseResult.ErrorMessage));
        }

        if (parseResult.Expression is null)
        {
            return (null, null);
        }

        var translationResult = filterService.Translate(parseResult.Expression, resource.WithoutHiddenSchemaFields());
        return translationResult.IsSuccess
            ? (translationResult.SqlFilter, null)
            : (null, InvalidFilter(context, translationResult.ErrorMessage));
    }

    private static IResult InvalidFilter(HttpContext context, string? message)
        => StandardErrorHelpers.CreateBadRequest(context,
            ErrorMessages.Validation.InvalidParameter,
            [message ?? "Invalid filter syntax."]);
}

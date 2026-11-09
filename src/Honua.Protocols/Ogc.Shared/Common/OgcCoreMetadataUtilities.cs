// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;

namespace Honua.Protocols.Ogc.Common;

internal static class OgcCoreMetadataUtilities
{
    public static bool TryPrepareMetadataResponse(
        HttpContext context,
        string? f,
        IReadOnlySet<string> allowedQueryParameters,
        out string outputFormat,
        out IResult? errorResult)
    {
        var validationError = OgcCommonUtilities.ValidateQueryParameters(context.Request, allowedQueryParameters);
        if (validationError is not null)
        {
            outputFormat = MediaTypes.Json;
            errorResult = StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
            return false;
        }

        if (!OgcCommonUtilities.TryGetOutputFormat(f, context, isFeatureContent: false, out outputFormat, out var formatError))
        {
            errorResult = OgcCommonUtilities.CreateFormatError(context, formatError);
            return false;
        }

        errorResult = null;
        return true;
    }

    public static ImmutableArray<Link>.Builder BuildLandingPageLinks(
        HttpContext context,
        string basePath,
        string outputFormat)
    {
        return OgcCommonUtilities.BuildFormatLinks(
                context.Request,
                basePath,
                outputFormat,
                OgcCommonUtilities.MetadataFormats,
                "This document")
            .ToBuilder();
    }

    public static ImmutableArray<Link> BuildConformanceLinks(
        HttpContext context,
        string conformancePath,
        string outputFormat)
    {
        return OgcCommonUtilities.BuildFormatLinks(
            context.Request,
            conformancePath,
            outputFormat,
            OgcCommonUtilities.MetadataFormats,
            "Conformance declaration");
    }

    public static Task<IResult> GetOpenApiSpecAsync(
        HttpContext context,
        string? f,
        IWebHostEnvironment environment,
        IReadOnlySet<string> allowedQueryParameters,
        string documentName,
        string fallbackSpec)
    {
        return OgcOpenApiSpecUtilities.GetOpenApiSpecAsync(
            context,
            f,
            environment,
            allowedQueryParameters,
            documentName,
            fallbackSpec);
    }
}

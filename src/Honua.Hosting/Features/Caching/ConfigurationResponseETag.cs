// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Metadata.Abstractions;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Net.Http.Headers;

namespace Honua.Infrastructure.Caching;

internal static class ConfigurationResponseETag
{
    private static IServiceProvider? GetServices(HttpContext context)
    {
        IServiceProvider? services;
        try
        {
            services = context.RequestServices;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        return services;
    }

    internal static async ValueTask<string> ForResponseAsync(HttpContext context, string etag, string contentType)
    {
        var services = GetServices(context);
        etag = WithContentCoding(context, services, etag, contentType);
        var source = services?.GetService<IConfigurationGenerationSource>();
        if (source is null)
        {
            return etag;
        }

        var generation = await source.GetGenerationAsync(context.RequestAborted).ConfigureAwait(false);
        return string.Concat(etag.AsSpan(0, etag.Length - 1),
            "-g", generation.ToString(CultureInfo.InvariantCulture), "\"");
    }

    private static bool IsCompressible(HttpContext context, IResponseCompressionProvider compression, string contentType)
    {
        var originalContentType = context.Response.ContentType;
        try
        {
            context.Response.ContentType = contentType;
            return compression.ShouldCompressResponse(context);
        }
        finally
        {
            context.Response.ContentType = originalContentType;
        }
    }

    private static string WithContentCoding(HttpContext context, IServiceProvider? services, string etag, string contentType)
    {
        var compression = services?.GetService<IResponseCompressionProvider>();
        if (compression is null || !IsCompressible(context, compression, contentType))
        {
            return etag;
        }

        var vary = context.Response.Headers.GetCommaSeparatedValues(HeaderNames.Vary);
        if (!vary.Contains("*", StringComparer.OrdinalIgnoreCase) &&
            !vary.Contains(HeaderNames.AcceptEncoding, StringComparer.OrdinalIgnoreCase))
        {
            context.Response.Headers.AppendCommaSeparatedValues(HeaderNames.Vary, HeaderNames.AcceptEncoding);
        }

        var coding = context.Request.Headers.AcceptEncoding.Count == 0
            ? null
            : compression.GetCompressionProvider(context)?.EncodingName;
        if (coding is null)
        {
            return etag;
        }

        // The hash precedes compression, whose output can vary with compression settings.
        var coded = string.Concat(etag.AsSpan(0, etag.Length - 1), "-ce-", coding, "\"");
        return coded.StartsWith("W/", StringComparison.Ordinal) ? coded : "W/" + coded;
    }
}

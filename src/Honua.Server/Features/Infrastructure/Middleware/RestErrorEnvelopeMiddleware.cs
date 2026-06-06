// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Models;

namespace Honua.Infrastructure.Middleware;

/// <summary>
/// Emits the GeoServices/Esri error envelope for responses that terminate with a
/// bodyless 404/405/406/415/501 under the GeoServices REST surface. ASP.NET routing
/// short-circuits unmatched routes (404) and method mismatches (405) with an empty
/// body before any endpoint runs, so <see cref="GlobalExceptionMiddleware"/> (which
/// only catches thrown exceptions) never shapes them. This middleware re-shapes those
/// status-only responses into the Esri contract
/// (<c>{"error":{"code":...,"message":...,"details":[...]}}</c>) while preserving the
/// status code and any routing-set headers (e.g. <c>Allow</c> for 405).
/// </summary>
internal sealed class RestErrorEnvelopeMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    public async Task InvokeAsync(HttpContext context)
    {
        await _next(context).ConfigureAwait(false);

        // Only re-shape GeoServices REST surfaces; leave OGC/STAC/OData/admin and
        // non-/rest routes to their own contracts.
        if (!ProtocolRequestClassifier.IsGeoServices(context.Request.Path))
        {
            return;
        }

        // Cannot rewrite a response that has already begun streaming a body.
        if (context.Response.HasStarted)
        {
            return;
        }

        // Only act on bodyless status-only terminations. A non-null, non-zero
        // ContentLength means a handler already wrote an envelope/payload.
        if (context.Response.ContentLength is > 0)
        {
            return;
        }

        var status = context.Response.StatusCode;
        if (status is not (StatusCodes.Status404NotFound
            or StatusCodes.Status405MethodNotAllowed
            or StatusCodes.Status406NotAcceptable
            or StatusCodes.Status415UnsupportedMediaType
            or StatusCodes.Status501NotImplemented))
        {
            return;
        }

        var (title, detail) = status switch
        {
            StatusCodes.Status404NotFound => ("Not Found", "The requested operation or resource was not found."),
            StatusCodes.Status405MethodNotAllowed => ("Method Not Allowed", "The HTTP method is not supported for this operation."),
            StatusCodes.Status406NotAcceptable => ("Not Acceptable", "The requested representation is not available."),
            StatusCodes.Status415UnsupportedMediaType => ("Unsupported Media Type", "The request media type is not supported."),
            _ => ("Not Implemented", "The requested operation is not implemented."),
        };

        var errorResponse = new StandardErrorResponse(status, title, detail);
        await StandardErrorResponseFormatter.WriteErrorAsync(context, errorResponse).ConfigureAwait(false);
    }
}

/// <summary>
/// Registration extension for <see cref="RestErrorEnvelopeMiddleware"/>.
/// </summary>
internal static class RestErrorEnvelopeMiddlewareExtensions
{
    public static IApplicationBuilder UseRestErrorEnvelope(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<RestErrorEnvelopeMiddleware>();
    }
}

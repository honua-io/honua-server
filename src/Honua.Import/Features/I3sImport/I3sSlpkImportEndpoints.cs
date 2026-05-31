// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Import.Features.I3sImport;

/// <summary>
/// Admin endpoints that convert Esri I3S/.slpk scene layers into Honua 3D
/// Tiles tilesets and register them for serving.
/// </summary>
internal static class I3sSlpkImportEndpoints
{
    /// <summary>
    /// Maps the I3S/.slpk import endpoint into the supplied app builder.
    /// </summary>
    /// <remarks>
    /// No-op when <see cref="ISceneRegistrationService"/> is not registered
    /// (non-Postgres provider profile, mirroring the
    /// <c>SceneDatasetEndpoints</c> guard). Without a registration service
    /// the converter cannot wire the output into the hosted-serving path,
    /// so the endpoint stays unmapped instead of writing files that no scene
    /// route can resolve.
    /// </remarks>
    public static IEndpointRouteBuilder MapI3sSlpkImportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var serviceInspector = endpoints.ServiceProvider.GetService<IServiceProviderIsService>();
        if (serviceInspector is not null && !serviceInspector.IsService(typeof(ISceneRegistrationService)))
        {
            return endpoints;
        }

        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/import/i3s-slpk")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("I3sSlpkImport")
            .RequireAdminAuthorization();

        _ = group.MapPost("/", HandleImport)
            .WithName("ImportI3sSlpk")
            .WithSummary("Convert an Esri I3S/.slpk scene layer into a Honua 3D Tiles tileset and register it.");

        return endpoints;
    }

    private static async Task HandleImport(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;

        I3sSlpkImportRequest? request;
        try
        {
            request = await context.Request
                .ReadFromJsonAsync(I3sImportApiJsonContext.Default.I3sSlpkImportRequest, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Invalid request body.", StatusCodes.Status400BadRequest);
            return;
        }

        if (request is null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Request body is required.", StatusCodes.Status400BadRequest);
            return;
        }

        var service = context.RequestServices.GetRequiredService<I3sSlpkImportService>();
        var createdBy = ResolveCreatedBy(context);

        I3sSlpkImportResult result;
        try
        {
            result = await service.ImportAsync(request, createdBy, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "I3S import was canceled by the operator.",
                StatusCodes.Status499ClientClosedRequest);
            return;
        }

        if (!result.Success)
        {
            await Results.Json(result, I3sImportApiJsonContext.Default.I3sSlpkImportResult,
                    statusCode: StatusCodes.Status400BadRequest)
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        await Results.Json(result, I3sImportApiJsonContext.Default.I3sSlpkImportResult,
                statusCode: StatusCodes.Status200OK)
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private static string ResolveCreatedBy(HttpContext context)
        => context.User.Identity?.Name is { Length: > 0 } name ? name : "i3s-import";
}

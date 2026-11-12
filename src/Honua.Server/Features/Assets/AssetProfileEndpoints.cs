// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Assets;
using Honua.Infrastructure.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Assets;

internal static class AssetProfileEndpoints
{
    public static IEndpointRouteBuilder MapAssetProfileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var serviceInspector = endpoints.ServiceProvider.GetService<IServiceProviderIsService>();
        if (serviceInspector is not null && !serviceInspector.IsService(typeof(IAssetProfileStore)))
        {
            return endpoints;
        }

        var profiles = endpoints.MapGroup("/api/v{version:apiVersion}/assets/profiles")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Assets")
            .RequireAdminAuthorization();

        profiles.MapGet("", HandleList)
            .WithName("ListAssetProfiles")
            .WithSummary("List the asset profiles of a service.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces<AssetProfileListResult>()
            .Produces<AssetValidationProblem>(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        profiles.MapPost("", HandleCreate)
            .WithName("CreateAssetProfile")
            .WithSummary("Profile an existing point, line or polygon layer as an asset layer (idempotent by commandId).")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]))
            .Accepts<AssetProfileWriteRequest>("application/json")
            .Produces<AssetProfileCommandResponse>(StatusCodes.Status201Created)
            .Produces<AssetValidationProblem>(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        profiles.MapGet("/{profileId:guid}", HandleGet)
            .WithName("GetAssetProfile")
            .WithSummary("Get an asset profile.")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces<AssetProfile>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        profiles.MapPut("/{profileId:guid}", HandleUpdate)
            .WithName("UpdateAssetProfile")
            .WithSummary("Update an asset profile at an expected version (idempotent by commandId).")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Put]))
            .Accepts<AssetProfileWriteRequest>("application/json")
            .Produces<AssetProfileCommandResponse>()
            .Produces<AssetValidationProblem>(StatusCodes.Status400BadRequest)
            .Produces<AssetCommandConflict>(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static Task<IResult> HandleList(HttpContext context, [FromServices] AssetProfileService service)
        => service.ListAsync(context);

    private static Task<IResult> HandleCreate(HttpContext context, [FromServices] AssetProfileService service)
        => service.CreateAsync(context);

    private static Task<IResult> HandleGet(HttpContext context, [FromServices] AssetProfileService service, Guid profileId)
        => service.GetAsync(context, profileId);

    private static Task<IResult> HandleUpdate(HttpContext context, [FromServices] AssetProfileService service, Guid profileId)
        => service.UpdateAsync(context, profileId);
}

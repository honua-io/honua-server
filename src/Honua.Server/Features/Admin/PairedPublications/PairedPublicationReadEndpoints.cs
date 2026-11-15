// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.FileStorage;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin.PairedPublications;

internal static class PairedPublicationReadEndpoints
{
    internal static IEndpointRouteBuilder MapPairedPublicationReadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/publications/{publicationId}")
            .WithTags("Publications").CacheOutput(policy => policy.NoCache());
        group.MapMethods("/current.json", ["GET", "HEAD"], CurrentAsync)
            .WithName("GetCurrentPairedPublication")
            .WithSummary("Resolve a paired publication to its immutable descriptor URL");
        group.MapMethods("/{version}/{fileName}", ["GET", "HEAD"], VersionAsync)
            .WithName("GetPairedPublicationArtifact")
            .WithSummary("Read an authorized paired publication descriptor or artifact with byte ranges");
        return endpoints;
    }

    private static async Task<IResult> CurrentAsync(
        string publicationId, HttpContext context, [FromServices] PairedPublicationReader reader, CancellationToken cancellationToken)
    {
        var unauthorized = RequireAuthentication(context);
        if (unauthorized is not null)
        {
            return unauthorized;
        }
        var target = await reader.ResolveAsync(publicationId, null, cancellationToken).ConfigureAwait(false);
        var error = await RequireAccessAsync(context, target, cancellationToken).ConfigureAwait(false);
        return error ?? Results.Redirect(
            $"{context.Request.PathBase}/api/v1/publications/{publicationId}/{target!.Descriptor.Version}/descriptor.json",
            permanent: false, preserveMethod: true);
    }

    private static async Task<IResult> VersionAsync(
        string publicationId, string version, string fileName, HttpContext context,
        [FromServices] PairedPublicationReader reader, [FromServices] ArtifactByteReader byteReader, CancellationToken cancellationToken)
    {
        var unauthorized = RequireAuthentication(context);
        if (unauthorized is not null)
        {
            return unauthorized;
        }
        var target = await reader.ResolveAsync(publicationId, version, cancellationToken).ConfigureAwait(false);
        var error = await RequireAccessAsync(context, target, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }
        if (!PairedPublicationReader.IsAllowedFile(target!.Descriptor, fileName))
        {
            return Results.NotFound();
        }
        var metadata = await reader.GetFileAsync(target, fileName, cancellationToken).ConfigureAwait(false);
        return metadata is null ? Results.NotFound() :
            await ArtifactHttpResponse.ServeAsync(context, metadata, byteReader, cancellationToken).ConfigureAwait(false);
    }

    private static IResult? RequireAuthentication(HttpContext context)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        if (context.User.Identity?.IsAuthenticated == true)
        {
            return null;
        }
        AccessPolicyHelpers.AppendAuthenticationChallenge(context);
        return StandardErrorHelpers.CreateUnauthorized(context, AccessPolicyHelpers.AuthRequiredMessage);
    }

    private static async Task<IResult?> RequireAccessAsync(
        HttpContext context, PairedPublicationReadTarget? target, CancellationToken cancellationToken)
    {
        if (target is null || !TenantScopeHelpers.IsPublicationVisible(
            context, target.Source.Publication, target.Source.Resource, target.Source.Service))
        {
            return Results.NotFound();
        }
        return await AccessPolicyHelpers.RequireResourceAccessAsync(
            context, target.Source.Resource, AuthorizationOperation.Export, target.Source.Service, cancellationToken).ConfigureAwait(false);
    }
}

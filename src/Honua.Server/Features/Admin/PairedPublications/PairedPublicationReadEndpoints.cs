// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.SharedCaching;
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
            .WithSharedCacheClass(ResponseCacheClass.StaticObject)
            .WithName("GetPairedPublicationArtifact")
            .WithSummary("Read an authorized paired publication descriptor or artifact with byte ranges");
        return endpoints;
    }

    private static async Task<IResult> CurrentAsync(
        string publicationId, HttpContext context, [FromServices] PairedPublicationReader reader, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        var target = await reader.ResolveCurrentAsync(publicationId, cancellationToken).ConfigureAwait(false);
        var error = await RequireAccessAsync(context, target, cancellationToken).ConfigureAwait(false);
        return error ?? Results.Redirect(
            $"{context.Request.PathBase}/api/v1/publications/{publicationId}/{target!.Descriptor.Version}/descriptor.json",
            permanent: false, preserveMethod: true);
    }

    private static async Task<IResult> VersionAsync(
        string publicationId, string version, string fileName, HttpContext context,
        [FromServices] PairedPublicationReader reader, [FromServices] ArtifactByteReader byteReader, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        var current = await reader.ResolveCurrentAsync(publicationId, cancellationToken).ConfigureAwait(false);
        var error = await RequireAccessAsync(context, current, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }
        var target = await reader.ResolvePinnedAsync(current!, version, cancellationToken).ConfigureAwait(false);
        if (target is null || !PairedPublicationReader.IsAllowedFile(target.Descriptor, fileName))
        {
            return Results.NotFound();
        }
        var metadata = await reader.GetFileAsync(target, fileName, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            return Results.NotFound();
        }
        SharedCacheResponse.UsePublication(context, current!.Source.Service, current.Source.Publication);
        if (SharedCacheGuard.RequestMayBeShared(context))
        {
            context.Response.Headers.Remove("Cache-Control");
        }
        return await ArtifactHttpResponse.ServeConfigurationBoundAsync(
            new(context, metadata, byteReader), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult?> RequireAccessAsync(
        HttpContext context, PairedPublicationReadTarget? target, CancellationToken cancellationToken)
    {
        if (target is null && context.User.Identity?.IsAuthenticated != true)
        {
            AccessPolicyHelpers.AppendAuthenticationChallenge(context);
            return StandardErrorHelpers.CreateUnauthorized(context, AccessPolicyHelpers.AuthRequiredMessage);
        }
        if (target is null || !TenantScopeHelpers.IsPublicationVisible(
            context, target.Source.Publication, target.Source.Resource, target.Source.Service))
        {
            return Results.NotFound();
        }
        var accessError = await AccessPolicyHelpers.RequireResourceAccessAsync(
            context, target.Source.Resource, AuthorizationOperation.Export, target.Source.Service, cancellationToken).ConfigureAwait(false);
        if (accessError is not null)
        {
            return accessError;
        }

        // Immutable archives cannot apply request-specific read policies. Resolve the
        // same current policy as feature reads before redirects, conditions or bytes.
        var services = context.RequestServices;
        var security = new LayerReadSecurityResolver(
            null,
            services.GetRequiredService<IFilterExpressionService>(),
            services.GetRequiredService<IRowLevelSecurityFilterSource>(),
            services.GetRequiredService<IFieldMaskSource>());
        var query = await security.ApplyAsync(target.Source.Resource, new FeatureQuery(), cancellationToken).ConfigureAwait(false);
        return query.EnforcedSqlFilter is not null || query.EnforcedMaskedFields is { IsDefaultOrEmpty: false }
            ? StandardErrorHelpers.CreateForbidden(context, "This publication cannot satisfy the current read policy.")
            : null;
    }
}

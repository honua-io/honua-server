// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.MultiTenancy;
using Honua.Core.Features.Styling;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Protocols.Ogc.Api.Styles.Models;
using Honua.Protocols.Ogc.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Honua.Protocols.Ogc.Api.Styles;

public static partial class OgcStylesEndpoints
{
    private static void MapSharedStyleImpact(RouteGroupBuilder group)
    {
        group.MapGet("/{styleId}/impact", GetSharedStyleImpact)
            .WithDisplayName("Get Shared Style Impact")
            .WithName("GetSharedStyleImpact")
            .WithSummary("Get the shared-style review required before save")
            .WithDescription("Returns the stored style version, a strong entity tag, and the associations and consumers in the caller's tenant. Requires style management rights. Not cached: the review differs by tenant.")
            .Produces<SharedStyleImpactResponse>(StatusCodes.Status200OK, MediaTypes.Json)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAdminAuthorization();
    }

    private static async Task<IResult> GetSharedStyleImpact(
        string styleId,
        HttpContext context,
        IOgcStyleProjection projection)
    {
        var graph = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var accessError = await AuthorizeStyleAsync(context, styleId, graph, context.RequestAborted).ConfigureAwait(false);
        if (accessError is not null)
        {
            return accessError;
        }

        var impact = await projection.GetSharedStyleImpactAsync(ImpactRequest(context, styleId), context.RequestAborted)
            .ConfigureAwait(false);
        if (impact is null)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Style '{styleId}' not found.");
        }

        // The route requires the same admin policy as the style PUT, so a caller who can read the review can save.
        var response = ToImpactResponse(impact) with { CanEdit = true };
        var json = JsonSerializer.Serialize(response, OgcStylesJsonContext.Default.SharedStyleImpactResponse);
        return new VersionedTextResult(json, MediaTypes.Json, impact.EntityTag);
    }

    private static SharedStyleImpactResponse ToImpactResponse(SharedStyleImpact impact) =>
        new()
        {
            StyleId = impact.StyleId,
            StyleVersion = impact.ReportedVersion,
            Etag = impact.EntityTag,
            ContentHash = impact.ContentHash,
            ImpactHash = impact.ImpactHash,
            Associations = impact.Associations.Select(ToAssociationResponse).ToArray(),
            Consumers = impact.Consumers.Select(ToConsumerResponse).ToArray(),
            CanEdit = false,
            ImpactComplete = impact.ImpactComplete
        };

    private static SharedStyleAssociationResponse ToAssociationResponse(SharedStyleAssociation association) =>
        new()
        {
            ResourceId = association.ResourceId,
            Name = association.Name,
            Title = association.Title,
            ServiceId = association.ServiceId
        };

    private static SharedStyleConsumerResponse ToConsumerResponse(SharedStyleConsumerRow consumer) =>
        new()
        {
            Kind = consumer.Kind,
            ItemId = consumer.ItemId,
            PackageKey = consumer.PackageKey,
            ContentHash = consumer.ContentHash,
            PublicationState = consumer.PublicationState,
            VersionId = consumer.VersionId,
            DraftId = consumer.DraftId,
            Generation = consumer.Generation,
            IsCurrent = consumer.IsCurrent
        };

    // Headers are optional. A save that sends neither stays the unconditional manage-styles PUT.
    // A presented If-Match or impact hash that is not the current review is 412 and is not stored.
    private static async Task<IResult?> RejectStaleStyleAsync(
        string styleId,
        HttpContext context,
        IOgcStyleProjection projection)
    {
        var hasMatch = context.Request.Headers.ContainsKey(HeaderNames.IfMatch);
        var hasImpact = context.Request.Headers.ContainsKey("X-Honua-Style-Impact");
        if (!hasMatch && !hasImpact)
        {
            return null;
        }

        var impact = await projection.GetSharedStyleImpactAsync(ImpactRequest(context, styleId), context.RequestAborted)
            .ConfigureAwait(false);
        var presentedImpact = context.Request.Headers["X-Honua-Style-Impact"].ToString();
        if (impact is null
            || (hasMatch && !IfMatchAllows(context.Request.Headers.IfMatch, impact.EntityTag))
            || (hasImpact && !string.Equals(presentedImpact, impact.ImpactHash, StringComparison.Ordinal)))
        {
            return StandardErrorHelpers.CreatePreconditionFailed(
                context,
                "The shared style changed. Reload it and review the current impact before saving.");
        }

        return null;
    }

    // The review covers the caller's tenant only, and the save check uses the same scope so the
    // impact hash a caller was shown is the one its save is compared against.
    private static SharedStyleImpactRequest ImpactRequest(HttpContext context, string styleId)
    {
        var requestTenant = TenantScopeHelpers.ResolveRequestTenantId(context);
        var options = context.RequestServices.GetService<IOptionsMonitor<TenantIsolationOptions>>()?.CurrentValue
            ?? new TenantIsolationOptions();
        return new SharedStyleImpactRequest
        {
            StyleId = styleId,
            ConsumerIndex = context.RequestServices.GetService<ISharedStyleConsumerIndex>(),
            ConsumerTenant = TenantOwnership.CreateScopeFilter(context.User, requestTenant, options),
            RequestTenant = requestTenant
        };
    }

    private static string? StoredStyleEntityTag(OgcStylesheet stylesheet)
    {
        if (stylesheet.CanonicalMapLibreJson is not { Length: > 0 } canonical)
        {
            return null;
        }

        return SharedStyleIdentity.EntityTag(stylesheet.StoredVersion, SharedStyleIdentity.ContentHash(canonical));
    }

    private static bool IfMatchAllows(StringValues header, string currentEntityTag)
    {
        if (header.Count == 0
            || !EntityTagHeaderValue.TryParseList(header, out IList<EntityTagHeaderValue>? tags)
            || tags is null
            || !EntityTagHeaderValue.TryParse(currentEntityTag, out var current))
        {
            return false;
        }

        foreach (var tag in tags)
        {
            if (tag.IsWeak)
            {
                continue;
            }

            if (tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(current, useStrongComparison: true))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class VersionedTextResult : IResult
    {
        private readonly string _content;
        private readonly string _mediaType;
        private readonly string? _entityTag;

        public VersionedTextResult(string content, string mediaType, string? entityTag)
        {
            _content = content;
            _mediaType = mediaType;
            _entityTag = entityTag;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            httpContext.Response.ContentType = _mediaType + "; charset=utf-8";
            if (_entityTag is not null && EntityTagHeaderValue.TryParse(_entityTag, out var parsed))
            {
                httpContext.Response.GetTypedHeaders().ETag = parsed;
            }

            await httpContext.Response.WriteAsync(_content, Encoding.UTF8).ConfigureAwait(false);
        }
    }
}

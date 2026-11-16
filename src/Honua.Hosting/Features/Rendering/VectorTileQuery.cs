// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Rendering;

// The query a served tile runs. A tile carries every attribute the provider selects, so the
// layer's hidden fields are masked before the provider sees the query. Tile routes can only
// obtain one through ForLayerAsync.
internal sealed class VectorTileQuery
{
    private VectorTileQuery(FeatureQuery value)
    {
        Value = value;
    }

    internal FeatureQuery Value { get; }

    // True when the request filters on a field the caller may not read, including a time
    // filter on a hidden time field: a hidden field cannot select rows either.
    internal bool IsRefused { get; private init; }

    internal static async Task<VectorTileQuery> ForLayerAsync(
        HttpContext context,
        MetadataV2Object resource,
        FeatureQuery query)
    {
        var masked = await MaskAsync(context, resource, query).ConfigureAwait(false);
        try
        {
            FeatureQuerySecurity.Validate(masked);
            return new VectorTileQuery(masked);
        }
        catch (ArgumentException)
        {
            // Validate reports a filter on a masked field this way; the route answers 400.
            return new VectorTileQuery(masked) { IsRefused = true };
        }
    }

    // The provider resolves the caller's field masks only when the query carries none, so the
    // hidden fields and those masks are stamped together.
    private static async Task<FeatureQuery> MaskAsync(HttpContext context, MetadataV2Object resource, FeatureQuery query)
    {
        var hidden = resource.HiddenSchemaFieldNames();
        if (hidden.Count == 0)
        {
            return query;
        }

        var unavailable = new HashSet<string>(hidden, StringComparer.OrdinalIgnoreCase);
        if (query.EnforcedMaskedFields is { IsDefaultOrEmpty: false } enforced)
        {
            unavailable.UnionWith(enforced);
        }
        else if (context.RequestServices.GetService<IFieldMaskSource>() is { } maskSource)
        {
            unavailable.UnionWith(
                await maskSource.ResolveAsync(resource, context.RequestAborted).ConfigureAwait(false));
        }

        return query with { EnforcedMaskedFields = [.. unavailable] };
    }
}

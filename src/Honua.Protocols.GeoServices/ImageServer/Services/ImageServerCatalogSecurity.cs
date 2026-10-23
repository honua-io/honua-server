// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

/// <summary>Shares field-policy enforcement across ImageServer catalog read surfaces.</summary>
internal static class ImageServerCatalogSecurity
{
    public static Task<ImmutableArray<string>> ResolveMasksAsync(
        HttpContext context, MetadataV2Resource? resource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return context.RequestServices.GetRequiredService<IFieldMaskSource>()
            .ResolveAsync(resource, cancellationToken);
    }

    public static async Task<ImmutableArray<string>> ResolveMasksAsync(
        HttpContext context, int layerId, CancellationToken cancellationToken)
    {
        var snapshot = await context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>()
            .GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var resolved = ImageServerV2Lookups.FindByStorageLayerId(snapshot, layerId, context);
        return await ResolveMasksAsync(context, resolved?.Resource, cancellationToken).ConfigureAwait(false);
    }

    public static bool IsMasked(ImmutableArray<string> maskedFields, string field)
        => !maskedFields.IsDefaultOrEmpty && maskedFields.Contains(field, StringComparer.OrdinalIgnoreCase);

    public static void Validate(ImageServerCatalogQuery query, ImmutableArray<string> maskedFields)
    {
        try
        {
            FeatureQuerySecurity.Validate(new FeatureQuery
            {
                Where = query.Where,
                OrderBy = query.OrderBy.Select(clause => clause.Descending
                    ? OrderByClause.Desc(clause.Field)
                    : OrderByClause.Asc(clause.Field)).ToImmutableArray(),
                TemporalFilter = query.Time.HasValue || query.TimeStart.HasValue
                    ? new TemporalFilter { PropertyName = "AcquisitionDate", PropertyType = TemporalPropertyType.DateTime }
                    : null,
                EnforcedMaskedFields = maskedFields
            });
        }
        catch (ArgumentException)
        {
            throw new ImageServerCatalogFilterException("Query references a masked catalog field.");
        }
    }
}

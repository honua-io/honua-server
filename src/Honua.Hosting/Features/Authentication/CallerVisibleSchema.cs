// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Authentication;

// The schema a caller may see: the caller's masked fields are marked hidden, so every listing that omits hidden fields omits them too.
internal static class CallerVisibleSchema
{
    public static async Task<MetadataV2Object> HideMaskedFieldsAsync(
        HttpContext context, MetadataV2Object resource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resource);
        if (context.RequestServices.GetService<IFieldMaskSource>() is not { } masks)
        {
            return resource;
        }

        var masked = await masks.ResolveAsync(resource, cancellationToken).ConfigureAwait(false);
        return resource.WithSchemaFieldsHidden(masked);
    }
}

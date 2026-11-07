// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Helpers;

namespace Honua.Migration;

internal static class GeoservicesImportPlanning
{
    public static async Task<IReadOnlyList<GeoservicesImportTarget>> PlanAsync(
        HttpContext context, IReadOnlyList<GeoservicesImportSelection> selections)
    {
        var tenant = context.RequestServices.GetRequiredService<ITenantContext>();
        var provider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await provider.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
        return GeoservicesImportTargetPlanner.PlanBatch(snapshot.Graph, selections, tenant.TenantId)
            .Select(target => target with { TenantSource = tenant.Source }).ToArray();
    }

    public static void RestoreTenant(IServiceProvider services, GeoservicesImportTarget? target)
    {
        if (target is not null)
            services.GetRequiredService<RequestTenantContext>().Set(target.TenantId, target.TenantSource);
    }

    public static Task WriteErrorAsync(HttpContext context, LayerPublishingException exception)
        => AdminResponseWriter.WriteErrorAsync(context, exception.Message, exception.ErrorKind switch
        {
            LayerPublishingErrorKind.NotFound => StatusCodes.Status404NotFound,
            LayerPublishingErrorKind.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        });
}

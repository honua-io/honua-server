// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Protocols.GeoServices.FeatureServer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Protocols.GeoServices.VersionManagementServer;

internal static class VersionManagementAuthorization
{
    public static async Task<IResult?> RequireServiceWriteAccessAsync(
        string serviceId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var validation = await FeatureServerResourceValidationHelpers.ValidateServiceV2Async(
            resourceValidator, serviceId, context, logger: null, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return validation.ErrorResult!;
        }

        var service = validation.Service!;
        var accessError = await AccessPolicyHelpers.RequireServiceAccessAsync(
            context, service, AuthorizationOperation.Update, cancellationToken).ConfigureAwait(false);
        if (accessError is not null)
        {
            return accessError;
        }

        return await ServiceDataEditorAuthorization.RequireServiceDataEditorAsync(
            context, service, cancellationToken).ConfigureAwait(false);
    }
}

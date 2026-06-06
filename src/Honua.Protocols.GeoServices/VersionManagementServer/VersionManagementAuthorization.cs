// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Protocols.GeoServices.FeatureServer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Protocols.GeoServices.VersionManagementServer;

/// <summary>
/// Authorization helper for the VersionManagementServer surface. Branch-version lifecycle
/// operations mutate edit state, so they require the same service-level write authorization the
/// FeatureServer edit/replication surface enforces (resource update access + data-editor RBAC).
/// This mirrors <c>FeatureServerEndpoints.RequireServiceWriteAccessBeforeBodyAsync</c> without
/// coupling the slice to FeatureServer-private members.
/// </summary>
internal static class VersionManagementAuthorization
{
    /// <summary>
    /// Validates the service and enforces service write authorization. Returns a non-null result to
    /// short-circuit on validation/authorization failure, or null when the caller may proceed.
    /// </summary>
    /// <param name="serviceId">The feature service id.</param>
    /// <param name="context">The HTTP context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A failure <see cref="IResult"/>, or null when authorized.</returns>
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

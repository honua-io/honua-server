// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Licensing;
using Honua.Infrastructure.Models;
using Honua.Protocols.GeoServices.VersionManagementServer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Shared adapter for threading the GeoServices <c>gdbVersion</c> parameter into the canonical
/// query/edit pipeline (#1272, ADR-0051). Resolves the requested version through the provider's
/// <see cref="IVersionManager"/>:
/// <list type="bullet">
/// <item>absent / empty / <c>SDE.DEFAULT</c> resolves to <see cref="VersionContext.Default"/> with no
/// entitlement check, preserving the byte-identical non-versioned DEFAULT path (CITE-protected);</item>
/// <item>a named version is Pro-gated and Postgres-only, and must exist, otherwise an
/// Esri-style error result is returned.</item>
/// </list>
/// </summary>
internal static class FeatureServerVersioning
{
    /// <summary>
    /// Resolves the <c>gdbVersion</c> parameter for an edit request.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="canonicalServiceId">Canonical ID from the validated service metadata.</param>
    /// <param name="gdbVersion">The requested version identity, or null/empty for DEFAULT.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The resolved <see cref="VersionContext"/> (DEFAULT when absent) and a null error on success;
    /// or a default context and a non-null error result when gating/lookup fails.
    /// </returns>
    public static Task<(VersionContext? Version, IResult? Error)> ResolveEditVersionAsync(
        HttpContext context,
        string canonicalServiceId,
        string? gdbVersion,
        CancellationToken cancellationToken)
        => ResolveAsync(context, canonicalServiceId, gdbVersion, forEdit: true, cancellationToken);

    /// <summary>
    /// Resolves the <c>gdbVersion</c> parameter for a query request with the same provider/license
    /// gates as edits and the shared branch visibility policy. DEFAULT has no read overlay.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="canonicalServiceId">Canonical ID from the validated service metadata.</param>
    /// <param name="gdbVersion">The requested version identity, or null/empty for DEFAULT.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved context and a null error on success, or an error result on failure.</returns>
    public static Task<(VersionContext? Version, IResult? Error)> ResolveQueryVersionAsync(
        HttpContext context,
        string canonicalServiceId,
        string? gdbVersion,
        CancellationToken cancellationToken)
        => ResolveAsync(context, canonicalServiceId, gdbVersion, forEdit: false, cancellationToken);

    private static async Task<(VersionContext? Version, IResult? Error)> ResolveAsync(
        HttpContext context,
        string canonicalServiceId,
        string? gdbVersion,
        bool forEdit,
        CancellationToken cancellationToken)
    {
        // Fast DEFAULT path: no version requested. Do not touch the entitlement service or the
        // version manager so the non-versioned path stays byte-identical to pre-versioning behavior.
        if (string.IsNullOrWhiteSpace(gdbVersion))
        {
            return (VersionContext.Default, null);
        }

        var versionManager = context.RequestServices.GetRequiredService<IVersionManager>();
        if (DefaultVersionIdentity.IsDefaultName(gdbVersion))
        {
            var defaultContext = versionManager.SupportsVersioning
                ? await versionManager.ResolveAsync(gdbVersion, cancellationToken).ConfigureAwait(false)
                : VersionContext.Default;
            return defaultContext is { IsDefault: true }
                ? (VersionContext.Default, null)
                : (null, StandardErrorHelpers.CreateNotFound(context, $"Version '{gdbVersion}' was not found."));
        }
        if (Guid.TryParse(gdbVersion.Trim(), out var requestedId) && versionManager.SupportsVersioning &&
            (await versionManager.GetDefaultVersionIdentityAsync(cancellationToken).ConfigureAwait(false))?.VersionId == requestedId)
        {
            // DEFAULT retains ordinary service/resource/tenant/scoped-key permissions.
            // Its display owner is not an authorization identity and it has no branch overlay.
            return (VersionContext.Default, null);
        }

        var entitlementGate = LicenseGate.RequireEntitlement(
            context, FeatureCatalog.BranchVersioningKey, "Branch versioning");
        if (entitlementGate is not null)
        {
            return (null, entitlementGate);
        }

        if (!versionManager.SupportsVersioning)
        {
            return (null, StandardErrorHelpers.CreateNotImplemented(
                context,
                "Branch versioning is not supported by the configured data provider.",
                ["Branch versioning requires a PostgreSQL/PostGIS feature provider."]));
        }

        var resolved = await versionManager.ResolveForServiceAsync(canonicalServiceId, gdbVersion, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return (null, StandardErrorHelpers.CreateNotFound(
                context, $"Version '{gdbVersion}' was not found."));
        }

        if (!resolved.Value.IsDefault)
        {
            var descriptor = await versionManager.GetVersionAsync(
                resolved.Value.VersionId!.Value, cancellationToken).ConfigureAwait(false);
            var callerName = context.User?.Identity?.Name;
            var isAdmin = ServiceDataEditorAuthorization.IsAdminPrincipal(context);
            if (descriptor is null || !VersionAccessPolicy.IsVersionVisible(descriptor.Value, callerName, isAdmin))
            {
                // Do not let a data query confirm the existence of another user's private branch.
                return (null, StandardErrorHelpers.CreateNotFound(context, $"Version '{gdbVersion}' was not found."));
            }

            if (forEdit && !VersionAccessPolicy.CanEditVersionData(descriptor.Value, callerName, isAdmin))
            {
                return (null, StandardErrorHelpers.CreateForbidden(context, AccessPolicyHelpers.AccessForbiddenMessage));
            }
        }

        return (resolved.Value, null);
    }
}

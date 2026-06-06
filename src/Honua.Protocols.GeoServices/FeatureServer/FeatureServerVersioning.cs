// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Licensing;
using Honua.Infrastructure.Models;
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
/// <item>a named version is Enterprise-gated and Postgres-only, and must exist, otherwise an
/// Esri-style error result is returned.</item>
/// </list>
/// </summary>
internal static class FeatureServerVersioning
{
    /// <summary>
    /// Resolves the <c>gdbVersion</c> parameter for an edit request.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="gdbVersion">The requested version identity, or null/empty for DEFAULT.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The resolved <see cref="VersionContext"/> (DEFAULT when absent) and a null error on success;
    /// or a default context and a non-null error result when gating/lookup fails.
    /// </returns>
    public static Task<(VersionContext? Version, IResult? Error)> ResolveEditVersionAsync(
        HttpContext context,
        string? gdbVersion,
        CancellationToken cancellationToken)
        => ResolveAsync(context, gdbVersion, cancellationToken);

    /// <summary>
    /// Resolves the <c>gdbVersion</c> parameter for a query request. Identical gating to the edit
    /// path; the canonical read overlay is a no-op for DEFAULT.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="gdbVersion">The requested version identity, or null/empty for DEFAULT.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved context and a null error on success, or an error result on failure.</returns>
    public static Task<(VersionContext? Version, IResult? Error)> ResolveQueryVersionAsync(
        HttpContext context,
        string? gdbVersion,
        CancellationToken cancellationToken)
        => ResolveAsync(context, gdbVersion, cancellationToken);

    private static async Task<(VersionContext? Version, IResult? Error)> ResolveAsync(
        HttpContext context,
        string? gdbVersion,
        CancellationToken cancellationToken)
    {
        // Fast DEFAULT path: no version requested. Do not touch the entitlement service or the
        // version manager so the non-versioned path stays byte-identical to pre-versioning behavior.
        if (string.IsNullOrWhiteSpace(gdbVersion) ||
            gdbVersion.Trim().Equals("sde.default", StringComparison.OrdinalIgnoreCase))
        {
            return (VersionContext.Default, null);
        }

        var entitlementGate = LicenseGate.RequireEntitlement(
            context, FeatureCatalog.BranchVersioningKey, "Branch versioning");
        if (entitlementGate is not null)
        {
            return (null, entitlementGate);
        }

        var versionManager = context.RequestServices.GetRequiredService<IVersionManager>();
        if (!versionManager.SupportsVersioning)
        {
            return (null, StandardErrorHelpers.CreateNotImplemented(
                context,
                "Branch versioning is not supported by the configured data provider.",
                ["Branch versioning requires a PostgreSQL/PostGIS feature provider."]));
        }

        var resolved = await versionManager.ResolveAsync(gdbVersion, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return (null, StandardErrorHelpers.CreateNotFound(
                context, $"Version '{gdbVersion}' was not found."));
        }

        return (resolved.Value, null);
    }
}

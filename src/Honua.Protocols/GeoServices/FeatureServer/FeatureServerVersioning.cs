// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Licensing;
using Honua.Infrastructure.Models;
using Honua.Protocols.GeoServices.VersionManagementServer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Protocols.GeoServices.FeatureServer;

// An absent, empty, or SDE.DEFAULT version skips the entitlement check and stays on the non-versioned path; a named version is Pro-gated, Postgres-only, and must exist.
internal static class FeatureServerVersioning
{
    public static Task<(VersionContext? Version, IResult? Error)> ResolveEditVersionAsync(
        HttpContext context,
        string? gdbVersion,
        CancellationToken cancellationToken)
        => ResolveAsync(context, gdbVersion, cancellationToken);

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

        // A bare name prefers the caller's own branch. Qualified names, including ones whose
        // branch name contains dots, are matched as the full owner.name identity (#6140).
        var callerOwner = context.User?.Identity?.Name;
        var resolved = await versionManager.ResolveAsync(gdbVersion, callerOwner, cancellationToken).ConfigureAwait(false);
        if (resolved is not { } resolvedVersion)
        {
            return (null, StandardErrorHelpers.CreateNotFound(
                context, $"Version '{gdbVersion}' was not found."));
        }

        // ResolveAsync returns only the branch context, not its access policy.
        // Apply the same private-version visibility rule as VMS before the
        // context can select overlay rows (or target edits). A 404 conceals
        // private versions from callers who do not own them.
        var versions = await versionManager.ListAsync(cancellationToken).ConfigureAwait(false);
        var version = versions.FirstOrDefault(item => item.VersionId == resolvedVersion.VersionId);
        if (version.VersionId != resolvedVersion.VersionId ||
            !VersionAccessPolicy.IsVersionVisible(version, context.User?.Identity?.Name,
                ServiceDataEditorAuthorization.IsAdminPrincipal(context)))
        {
            return (null, StandardErrorHelpers.CreateNotFound(
                context, $"Version '{gdbVersion}' was not found."));
        }

        return (resolvedVersion, null);
    }
}

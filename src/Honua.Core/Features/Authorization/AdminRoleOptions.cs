// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Authorization;

/// <summary>
/// Config-bound set of role names recognized as the platform admin role, in addition to the
/// literal <c>admin</c> role which every admin check always recognizes unconditionally.
/// </summary>
/// <remarks>
/// <para>
/// Binds from the same <c>Oidc:AdminRoles</c> configuration key that
/// <c>Honua.Infrastructure.Authentication.OidcAuthenticationOptions.AdminRoles</c> and
/// <c>OidcAuthenticationExtensions.AddOidcAuthorization</c> use to widen the ASP.NET
/// <c>AdminPolicy</c>/<c>AdminPolicyAlias</c>/Temporal-* policies to configured OIDC admin-role
/// aliases (for example <c>administrator</c>). <c>Honua.Core</c> cannot reference
/// <c>Honua.Hosting</c>'s <c>OidcAuthenticationOptions</c> type directly (dependency direction),
/// so this type re-reads the identical config key rather than the identical config value's
/// consumer -- for any given deployment, both types resolve the exact same effective alias set,
/// keeping Core-level admin checks (for example <see
/// cref="Honua.Core.Features.Studio.Services.StudioAuthorizationService.IsAdmin"/>) in sync with
/// the ASP.NET policy layer's admin-role recognition without a duplicated, independently
/// maintained literal list.
/// </para>
/// </remarks>
public sealed class AdminRoleOptions
{
    /// <summary>The configuration section these options bind from (shared with OIDC options).</summary>
    public const string SectionName = "Oidc";

    /// <summary>
    /// Role names (matched via <see cref="System.Security.Claims.ClaimsPrincipal.IsInRole"/>,
    /// case-insensitively per that method's default comparison) that are recognized as the
    /// platform admin role, in addition to the literal <c>admin</c> role. Defaults to
    /// <c>["admin", "administrator"]</c>, matching <c>OidcAuthenticationOptions.AdminRoles</c>'s
    /// default.
    /// </summary>
    public string[] AdminRoles { get; set; } = ["admin", "administrator"];
}

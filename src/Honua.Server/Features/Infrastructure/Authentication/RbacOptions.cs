// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using System.Security.Claims;

namespace Honua.Server.Features.Infrastructure.Authentication;

/// <summary>
/// Configuration options for service-scoped RBAC checks.
/// </summary>
internal sealed class RbacOptions
{
    /// <summary>
    /// Configuration section name in appsettings.json.
    /// </summary>
    public const string SectionName = "Rbac";

    /// <summary>
    /// Gets or sets the claim type used for role values.
    /// </summary>
    public string RoleClaimType { get; set; } = "roles";

    /// <summary>
    /// Gets or sets global data editor roles that grant write access to all services.
    /// </summary>
    public string[] DataEditorRoles { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the prefix used for service-scoped data editor roles.
    /// Example: "data-editor:" supports roles like "data-editor:serviceA".
    /// </summary>
    public string DataEditorServicePrefix { get; set; } = "data-editor:";

    /// <summary>
    /// Gets the normalized role claim type for matching.
    /// </summary>
    internal string EffectiveRoleClaimType => string.IsNullOrWhiteSpace(RoleClaimType) ? ClaimTypes.Role : RoleClaimType;
}

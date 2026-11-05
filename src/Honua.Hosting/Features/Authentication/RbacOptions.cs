// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System;
using System.Security.Claims;

namespace Honua.Infrastructure.Authentication;

internal sealed class RbacOptions
{
    internal const string DefaultRoleClaimType = "roles";

    public const string SectionName = "Rbac";

    public string RoleClaimType { get; set; } = DefaultRoleClaimType;

    public string[] DataEditorRoles { get; set; } = Array.Empty<string>();

    public string DataEditorServicePrefix { get; set; } = "data-editor:";

    public string WorkspaceScopeClaimType { get; set; } = "groups";

    internal string EffectiveRoleClaimType => string.IsNullOrWhiteSpace(RoleClaimType) ? ClaimTypes.Role : RoleClaimType;
}

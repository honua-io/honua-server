// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Admin.Services;

internal sealed class AdminAuthorizationOptions
{
    public const string SectionName = "Authorization";

    public string RoleClaimType { get; init; } = "roles";

    public string[] AdminRoles { get; init; } = ["admin", "administrator"];
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Authorization;

public sealed class AdminRoleOptions
{
    public const string SectionName = "Oidc";

    public string[] AdminRoles { get; set; } = ["admin", "administrator"];
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.MultiTenancy;

// Core re-reads the same configuration key because it cannot reference the hosting options type.
public sealed class TenantIsolationOptions
{
    public const string SectionName = "MultiTenancy";

    public bool Enabled { get; set; } = true;

    public string? DefaultTenantId { get; set; } = "public";

    public string[] MultiTenantAdminRoles { get; set; } = ["multi_tenant_admin", "platform_admin"];
}

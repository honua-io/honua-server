// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.MultiTenancy;

public sealed class TenantContextOptions
{
    public const string SectionName = "MultiTenancy";

    public const string TenantHeaderName = "X-Honua-Tenant";

    public bool Enabled { get; set; } = true;

    public string[] TenantClaimTypes { get; set; } = ["tid", "tenant_id"];

    public string[] MultiTenantAdminRoles { get; set; } = ["multi_tenant_admin", "platform_admin"];

    public string? DefaultTenantId { get; set; } = "public";

    // The tenant id length bound stops log injection and pathological values.
    public int MaxTenantIdLength { get; set; } = 128;
}

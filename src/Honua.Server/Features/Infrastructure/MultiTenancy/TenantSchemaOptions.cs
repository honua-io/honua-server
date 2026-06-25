// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;

namespace Honua.Infrastructure.MultiTenancy;

/// <summary>
/// Configuration for schema-per-tenant routing (issue #346). Bound from the
/// <c>MultiTenancy:SchemaRouting</c> configuration section.
/// </summary>
/// <remarks>
/// Routing is <b>disabled by default</b> so that single-tenant deployments retain
/// byte-identical behavior: when <see cref="Enabled"/> is <see langword="false"/> the
/// tenant schema-routing middleware is not registered, no per-request
/// <c>SET search_path</c> override is issued, and the database uses its configured
/// default schema exactly as before.
/// </remarks>
public sealed class TenantSchemaOptions
{
    /// <summary>
    /// Configuration section name in appsettings.json.
    /// </summary>
    public const string SectionName = "MultiTenancy:SchemaRouting";

    /// <summary>
    /// Gets or sets whether schema-per-tenant routing is enabled. Defaults to
    /// <see langword="false"/> to preserve single-tenant behavior.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the prefix prepended to a tenant id to derive its schema name when no
    /// explicit mapping is present in <see cref="SchemaMap"/>. The derived schema is
    /// <c>{Prefix}{normalized-tenant-id}</c>, e.g. tenant <c>acme</c> -&gt; schema
    /// <c>tenant_acme</c>. Must itself be a valid SQL identifier prefix.
    /// </summary>
    public string SchemaPrefix { get; set; } = "tenant_";

    /// <summary>
    /// Gets or sets explicit tenant-id -&gt; schema-name overrides. Lookups are
    /// case-insensitive on the tenant id. Entries here take precedence over the derived
    /// <see cref="SchemaPrefix"/> form, allowing tenants whose ids do not normalize to a
    /// safe schema name to be routed explicitly.
    /// </summary>
    public Dictionary<string, string> SchemaMap { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets the tenant ids that are excluded from schema routing (they continue to
    /// use the connection's configured default schema). Typically the public/default tenant
    /// used for anonymous OGC reads so unauthenticated traffic is unaffected. Matched
    /// case-insensitively.
    /// </summary>
    public string[] UnroutedTenantIds { get; set; } = ["public"];
}

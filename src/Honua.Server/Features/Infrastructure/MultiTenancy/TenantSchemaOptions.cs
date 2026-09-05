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
    /// <c>{Prefix}{tenant-id}</c> in compatibility mode, e.g. tenant <c>acme</c> -&gt; schema
    /// <c>tenant_acme</c>. Must itself be a valid SQL identifier prefix.
    /// </summary>
    public string SchemaPrefix { get; set; } = "tenant_";

    /// <summary>
    /// Gets or sets explicit tenant-id -&gt; schema-name overrides. Lookups are
    /// exact and case-sensitive on the tenant id. Entries take precedence over derivation
    /// and reserve each schema for one tenant. Invalid or conflicting mappings fail closed.
    /// Use <see cref="SchemaMappings"/> for IDs containing configuration delimiters.
    /// </summary>
    public Dictionary<string, string> SchemaMap { get; set; } = new(System.StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets explicit mappings as values, supporting tenant IDs containing colons
    /// that cannot be represented as configuration dictionary keys. Combined with
    /// <see cref="SchemaMap"/>; duplicate tenant assignments to different schemas are rejected.
    /// </summary>
    public TenantSchemaMapping[] SchemaMappings { get; set; } = [];

    /// <summary>
    /// Gets or sets whether unmapped tenant IDs use reversible escaping. Defaults to false:
    /// compatibility mode preserves canonical legacy names and rejects IDs needing lossy
    /// normalization. Pin existing tenants with explicit mappings before enabling this option;
    /// changing it never migrates database schemas. Encoded names exceeding 63 bytes are rejected.
    /// </summary>
    public bool UseEncodedSchemaNames { get; set; }

    /// <summary>
    /// Gets or sets the tenant ids that are excluded from schema routing (they continue to
    /// use the connection's configured default schema). Typically the public/default tenant
    /// used for anonymous OGC reads so unauthenticated traffic is unaffected. Matched
    /// exactly and case-sensitively.
    /// </summary>
    public string[] UnroutedTenantIds { get; set; } = ["public"];
}

/// <summary>
/// An exact tenant identity and its operator-verified PostgreSQL schema.
/// </summary>
public sealed class TenantSchemaMapping
{
    /// <summary>Gets or sets the exact tenant identifier.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Gets or sets the existing or provisioned schema name.</summary>
    public string SchemaName { get; set; } = string.Empty;
}

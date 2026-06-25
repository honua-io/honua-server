// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using Microsoft.Extensions.Configuration;

namespace Honua.Core.Configuration;

/// <summary>
/// Resolves whether the database connection pool must operate in request-scoped schema mode —
/// where each request may issue a per-connection <c>SET search_path</c> override.
/// </summary>
/// <remarks>
/// Request-scoped schema mode is required by two features that share the same physical
/// mechanism:
///  - <c>HONUA_TEST_SCHEMA_HEADERS</c>: per-test schema isolation driven by the
///    <c>X-Honua-Test-Schema</c> header.
///  - <c>MultiTenancy:SchemaRouting:Enabled</c>: schema-per-tenant isolation (issue #346).
///
/// When either is enabled, Npgsql multiplexing is disabled and connections are reset on pool
/// return so per-request schema overrides cannot leak between requests. When both are
/// disabled (the single-tenant default) the pool keeps its configured default schema and
/// behavior is byte-identical to before this rail existed.
/// </remarks>
public static class RequestScopedSchemaConfiguration
{
    /// <summary>
    /// The legacy switch that enables per-test schema isolation via request headers.
    /// </summary>
    public const string TestSchemaHeadersKey = "HONUA_TEST_SCHEMA_HEADERS";

    /// <summary>
    /// The switch that enables schema-per-tenant routing (issue #346).
    /// </summary>
    public const string TenantSchemaRoutingKey = "MultiTenancy:SchemaRouting:Enabled";

    /// <summary>
    /// Returns <see langword="true"/> when any feature requiring request-scoped schema mode is
    /// enabled in the supplied configuration.
    /// </summary>
    /// <param name="configuration">The configuration root.</param>
    /// <returns><see langword="true"/> when request-scoped schema mode is required.</returns>
    public static bool IsEnabled(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetValue<bool>(TestSchemaHeadersKey)
            || configuration.GetValue<bool>(TenantSchemaRoutingKey);
    }
}

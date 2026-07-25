// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using System.Threading.Tasks;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.Infrastructure.MultiTenancy;

/// <summary>
/// Routes the current request to its tenant's PostgreSQL schema and records a usage signal
/// (issue #346). Runs after <c>TenantContextMiddleware</c> has resolved <see cref="ITenantContext"/>
/// and feeds the resolved schema into the request-scoped
/// <see cref="Honua.Core.Features.Infrastructure.Abstractions.ISchemaContext"/> so the existing
/// <c>SET search_path</c> data path enforces isolation.
/// </summary>
/// <remarks>
/// This middleware is only registered when <see cref="TenantSchemaOptions.Enabled"/> is
/// <see langword="true"/>. When disabled it is absent from the pipeline entirely, so
/// single-tenant deployments retain byte-identical behavior (no schema context is written and
/// the database keeps its configured default schema).
///
/// When a request carries an explicit test schema header (<c>X-Honua-Test-Schema</c>) the
/// schema context is already populated by <c>TestSchemaMiddleware</c>; this middleware never
/// overwrites a schema that another component already set, so test isolation continues to win.
/// </remarks>
internal sealed class TenantSchemaRoutingMiddleware(
    RequestDelegate next,
    ITenantSchemaResolver schemaResolver,
    ITenantUsageMeter usageMeter,
    ILogger<TenantSchemaRoutingMiddleware> logger)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private readonly ITenantSchemaResolver _schemaResolver = schemaResolver ?? throw new ArgumentNullException(nameof(schemaResolver));
    private readonly ITenantUsageMeter _usageMeter = usageMeter ?? throw new ArgumentNullException(nameof(usageMeter));
    private readonly ILogger<TenantSchemaRoutingMiddleware> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task InvokeAsync(HttpContext context)
    {
        var tenantContext = context.RequestServices.GetService<ITenantContext>();
        if (tenantContext is null || !tenantContext.RequireTenantId(out var tenantId, out _))
        {
            // No tenant resolved (anonymous with no default): leave the schema context untouched
            // so the connection uses its configured default schema. Behavior matches single-tenant.
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Usage-metering counter hook: attribute one request to the resolved tenant. This is the
        // instrumentation seam; aggregation/persistence/billing are deferred (#346).
        _usageMeter.RecordRequest(tenantId);

        // Resolve the tenant's schema and route to it unless a schema is already pinned
        // (e.g. by the test schema header) or the tenant is excluded from routing.
        var schemaContext = context.RequestServices.GetService<SchemaContext>();
        var routed = false;
        if (schemaContext is not null
            && string.IsNullOrWhiteSpace(schemaContext.CurrentSchema)
            && _schemaResolver.TryResolveSchema(tenantId, out var schemaName))
        {
            schemaContext.CurrentSchema = schemaName;
            routed = true;
            TenantSchemaRoutingLog.Routed(_logger, tenantId, schemaName, context.Request.Path.Value);
        }

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            // Clear the AsyncLocal-backed schema we set so it never leaks onto a pooled thread
            // after the request completes. Only clear what this middleware assigned.
            // The 'schemaContext is not null' check is redundant in practice (routed is only ever
            // set true after that same null check above), but the compiler's nullable-flow analysis
            // doesn't track that invariant across the intervening try/finally, so the check stays
            // to keep this a genuine null-safe dereference rather than a null-forgiving one.
            if (routed)
            {
                schemaContext!.CurrentSchema = null;
            }
        }
    }
}

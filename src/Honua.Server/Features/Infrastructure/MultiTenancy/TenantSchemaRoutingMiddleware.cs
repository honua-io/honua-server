// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System;
using System.Threading.Tasks;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.MultiTenancy;

// This middleware never overwrites a schema another component already set, so the test schema header wins.
internal sealed class TenantSchemaRoutingMiddleware(
    RequestDelegate next,
    ITenantSchemaResolver schemaResolver,
    ITenantUsageMeter usageMeter,
    ILogger<TenantSchemaRoutingMiddleware> logger,
    IOptions<TenantSchemaOptions> options)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private readonly ITenantSchemaResolver _schemaResolver = schemaResolver ?? throw new ArgumentNullException(nameof(schemaResolver));
    private readonly ITenantUsageMeter _usageMeter = usageMeter ?? throw new ArgumentNullException(nameof(usageMeter));
    private readonly ILogger<TenantSchemaRoutingMiddleware> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly TenantSchemaOptions _options = options.Value;

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
        if (!_options.UnroutedTenantIds.Contains(tenantId, StringComparer.Ordinal)
            && string.IsNullOrWhiteSpace(schemaContext?.CurrentSchema))
        {
            if (schemaContext is null || !_schemaResolver.TryResolveSchema(tenantId, out var schemaName))
            {
                await StandardErrorResponseFormatter.WriteErrorAsync(context, new StandardErrorResponse(
                    StatusCodes.Status503ServiceUnavailable,
                    "Tenant schema routing unavailable",
                    "Configure a unique tenant schema mapping or complete the documented schema migration."))
                    .ConfigureAwait(false);
                return;
            }

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

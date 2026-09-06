// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Abstractions;
using Honua.Infrastructure.Models;

namespace Honua.Infrastructure.Licensing;

/// <summary>Enforces the paid deployment license before data reads, exports and mutations.</summary>
internal sealed class LicenseOperationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ILicenseOperationPolicy policy)
    {
        // Recovery and health remain reachable; authorization on these routes still applies.
        var path = context.Request.Path;
        if (path.StartsWithSegments("/healthz") ||
            path.StartsWithSegments("/api/v1/admin/license") ||
            path.StartsWithSegments("/api/v1/admin/licenses") ||
            path.StartsWithSegments("/api/v1/admin/auth"))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var licenseCancellation = policy.OperationCancellation;
        if (policy.IsBlocked || licenseCancellation.IsCancellationRequested)
        {
            await DenyAsync(context).ConfigureAwait(false);
            return;
        }
        if (!licenseCancellation.CanBeCanceled)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var original = context.RequestAborted;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(original, licenseCancellation);
        using var abortStreaming = licenseCancellation.Register(() =>
        {
            if (context.Response.HasStarted)
            {
                context.Abort();
            }
        });
        var writingDenial = false;
        context.Response.OnStarting(() =>
        {
            if (!writingDenial)
            {
                licenseCancellation.ThrowIfCancellationRequested();
            }
            return Task.CompletedTask;
        });
        context.RequestAborted = linked.Token;
        try
        {
            await next(context).ConfigureAwait(false);
            if (licenseCancellation.IsCancellationRequested)
            {
                writingDenial = true;
                context.RequestAborted = original;
                await DenyAsync(context).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (licenseCancellation.IsCancellationRequested && !original.IsCancellationRequested)
        {
            writingDenial = true;
            context.RequestAborted = original;
            await DenyAsync(context).ConfigureAwait(false);
        }
        finally
        {
            context.RequestAborted = original;
        }
    }

    private static async Task DenyAsync(HttpContext context)
    {
        if (context.Response.HasStarted)
        {
            // Never finish a streamed partial read/export as a successful response.
            context.Abort();
            return;
        }
        context.Response.Clear();
        if (context.Request.ContentType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) == true)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/grpc";
            context.Response.ContentLength = 0;
            context.Response.Headers["grpc-status"] = "9"; // FAILED_PRECONDITION
            context.Response.Headers["grpc-message"] = "License unavailable or expired. Renew the configured license; re-validation runs every minute, or restart.";
            return;
        }
        await StandardErrorHelpers.CreatePaymentRequired(context,
            "License unavailable or expired. Renew the configured license; re-validation runs every minute, or restart.")
            .ExecuteAsync(context).ConfigureAwait(false);
    }
}

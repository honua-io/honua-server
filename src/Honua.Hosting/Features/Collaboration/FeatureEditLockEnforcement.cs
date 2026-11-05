// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Collaboration.FeatureLocks;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Collaboration;

// A lease is enforced here and again at the feature writer so a protocol without its own check cannot bypass it, the check is not inside the writer transaction, and enforcement is node-local.
public static class FeatureEditLockEnforcement
{
    public const string HolderHeaderName = "X-Honua-Lock-Holder";

    // A lease claimed with a session id counts as the caller's own only when the session ids match.
    public const string SessionHeaderName = "X-Honua-Lock-Session";

    public const string TenantHeaderName = "X-Honua-Lock-Tenant";

    public const FeatureEditConcurrencyPolicy EnforcedPolicy = FeatureEditConcurrencyPolicy.HonorActiveLock;

    // Protocol-specific graph service ids differ, so a lease is keyed on the shared routed service name and binds a competing write from another protocol.
    public static string? ResolveServiceName(
        MetadataV2Service? service,
        MetadataV2Publication? publication)
    {
        var name = service?.Metadata?.Name;
        return string.IsNullOrWhiteSpace(name) ? publication?.ServiceId : name;
    }

    public static int ResolveLayerId(MetadataV2Publication? publication, int fallbackLayerId)
        => publication?.LayerIndex ?? fallbackLayerId;

    // An unidentifiable holder fails any active lease instead of being allowed through.
    public static LockHolder? ResolveHolder(HttpContext? context)
    {
        if (context is null)
        {
            return null;
        }

        var principalName = context.User?.Identity?.IsAuthenticated == true
            ? context.User.Identity.Name
            : null;

        var holderId = Header(context, HolderHeaderName);
        if (string.IsNullOrWhiteSpace(holderId))
        {
            // A caller that claimed under its own identity does not have to echo anything.
            holderId = principalName;
        }

        return string.IsNullOrWhiteSpace(holderId)
            ? null
            : new LockHolder(
                holderId,
                DisplayName: null,
                SessionId: Header(context, SessionHeaderName),
                TenantId: Header(context, TenantHeaderName),
                // Authoritative, and the only part of this identity the caller cannot set.
                // The headers above select WHICH of the caller's own leases a write belongs
                // to; they cannot make a request the owner of someone else's lease, because
                // a lease claimed through the HTTP endpoints records the authenticated
                // principal and the guard requires that to match first.
                PrincipalName: principalName);
    }

    public static async ValueTask<bool> IsEvaluationRequiredAsync(
        IFeatureLockService? locks,
        CancellationToken cancellationToken = default)
        => locks is not null && await locks.HasAnyActiveLeasesAsync(cancellationToken).ConfigureAwait(false);

    public static async ValueTask<FeatureEditConflictResponse?> EvaluateAsync(
        IFeatureEditGuard? guard,
        string serviceName,
        int layerId,
        long objectId,
        string operation,
        LockHolder? holder,
        CancellationToken cancellationToken = default)
    {
        if (guard is null || string.IsNullOrWhiteSpace(serviceName))
        {
            return null;
        }

        var intent = new FeatureEditIntent(
            FeatureRef.Canonical(serviceName, layerId, objectId),
            operation,
            holder);

        var decision = await guard.EvaluateAsync(intent, EnforcedPolicy, cancellationToken).ConfigureAwait(false);
        return decision.IsAllowed ? null : decision.Conflict;
    }

    public static async ValueTask<FeatureEditConflictResponse?> EvaluateRequestAsync(
        HttpContext context,
        string? serviceName,
        int layerId,
        long objectId,
        string operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return null;
        }

        var locks = context.RequestServices?.GetService<IFeatureLockService>();
        if (!await IsEvaluationRequiredAsync(locks, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return await EvaluateAsync(
            context.RequestServices?.GetService<IFeatureEditGuard>(),
            serviceName,
            layerId,
            objectId,
            operation,
            ResolveHolder(context),
            cancellationToken).ConfigureAwait(false);
    }

    public static string Describe(FeatureEditConflictResponse conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);

        if (conflict.Lock is not { } held)
        {
            return conflict.Message;
        }

        var holder = string.IsNullOrWhiteSpace(held.Holder.DisplayName)
            ? held.Holder.HolderId
            : held.Holder.DisplayName;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"The feature is locked for editing by '{holder}' until {held.ExpiresAt:O}.");
    }

    private static string? Header(HttpContext context, string name)
        => context.Request.Headers.TryGetValue(name, out var values) && values.Count > 0
            ? values[0]
            : null;
}

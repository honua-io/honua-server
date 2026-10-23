// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Collaboration.FeatureLocks;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Collaboration;

/// <summary>
/// The single place every feature-write surface consults the collaborative-editing
/// lock guard (#4402).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <c>/collaboration/feature-locks</c> hands out editing
/// leases. Before #4402 nothing honoured them: <see cref="IFeatureEditGuard"/> was
/// registered in DI and covered by unit tests, but no write path resolved it, so a
/// lease was a row in a dictionary and a second editor could overwrite the holder's
/// feature freely. This helper is what makes a granted lease binding. The HTTP
/// handlers call it so each surface refuses in its own natural shape — GeoServices
/// <c>applyEdits</c> per-edit code 1005, OGC API Features and OData <c>423 Locked</c>
/// — and <see cref="FeatureLockEnforcingFeatureWriter"/> calls it again at the shared
/// <c>IFeatureWriter</c> boundary so a surface without its own check (WFS-T, gRPC, the
/// OData atomic change-set, or one added later) cannot route around a lease.
/// </para>
/// <para>
/// <b>What this is not.</b> The evaluation happens immediately before the mutation,
/// not inside the writer transaction, so a lease claimed after a competing request has
/// already begun may not stop that request. A lease is a coordination primitive
/// claimed before editing starts; the hard serialisation point remains the
/// optimistic-concurrency precondition the provider re-validates under its row lock.
/// </para>
/// <para>
/// <b>The enforced policy.</b>
/// <see cref="FeatureEditConcurrencyPolicy.HonorActiveLock"/>: an edit is rejected
/// only while <em>another</em> editor holds an active lease on the target feature.
/// Claiming a lease stays optional, so a client that never touches
/// <c>/feature-locks</c> behaves exactly as it did before; a client that did claim
/// one can no longer be overwritten. The stricter
/// <see cref="FeatureEditConcurrencyPolicy.RequireLock"/> and
/// <see cref="FeatureEditConcurrencyPolicy.RequireVersionToken"/> policies are not
/// selected by any 2026.1 write path — see
/// <c>docs/reference/collaboration/feature-locks.md</c> for the recorded scope.
/// </para>
/// <para>
/// <b>Feature identity.</b> A lease is keyed on
/// <c>(serviceName, layerId, featureId)</c> where <c>featureId</c> is the feature's
/// server-side OBJECTID rendered invariantly. Every protocol resolves its own public
/// identifier down to that OBJECTID before evaluating, so a lease claimed once is
/// honoured no matter which protocol the competing write arrives on.
/// </para>
/// <para>
/// <b>Scope.</b> Enforcement is node-local, because
/// <see cref="InMemoryFeatureLockService"/> is the only lease store that ships. A
/// lease held on one node does not block a write routed to another node. That limit
/// is stated in the capability manifest and in the documentation rather than being
/// left for an operator to discover.
/// </para>
/// </remarks>
public static class FeatureEditLockEnforcement
{
    /// <summary>
    /// Request header carrying the lock holder id the caller claimed its lease under.
    /// Defaults to the authenticated principal name when absent.
    /// </summary>
    /// <remarks>
    /// This header selects among the caller's <em>own</em> leases; it is not proof of
    /// ownership and forging it gains nothing. A lease claimed through the HTTP endpoints
    /// also records the authenticated principal, which no client can set, and the guard
    /// requires that to match before it treats a write as the holder's.
    /// </remarks>
    public const string HolderHeaderName = "X-Honua-Lock-Holder";

    /// <summary>
    /// Request header carrying the editing session id the caller claimed its lease
    /// under. Required when the lease was claimed with a session id, because a lease
    /// is only recognised as the caller's own when the session ids also match.
    /// </summary>
    public const string SessionHeaderName = "X-Honua-Lock-Session";

    /// <summary>
    /// Request header carrying the tenant id the caller claimed its lease under.
    /// </summary>
    public const string TenantHeaderName = "X-Honua-Lock-Tenant";

    /// <summary>The policy every 2026.1 HTTP write surface enforces.</summary>
    public const FeatureEditConcurrencyPolicy EnforcedPolicy = FeatureEditConcurrencyPolicy.HonorActiveLock;

    /// <summary>
    /// Resolves the lease-namespace service name for a published layer.
    /// </summary>
    /// <param name="service">The service the request routed through.</param>
    /// <param name="publication">The publication backing the edited layer.</param>
    /// <returns>The service name a client would claim its lease under.</returns>
    /// <remarks>
    /// The <em>name</em>, not the graph <c>ServiceId</c>. A client claims a lease with the
    /// identifier it sees in the URL — <c>/rest/services/{name}/FeatureServer/{layerId}</c> —
    /// and the graph gives every protocol its own service row for the same logical service
    /// (<c>svc-test-feature</c>, <c>svc-test-map</c>, …) while sharing one name. Keying on the
    /// name is therefore both what the client can know and what makes a lease claimed once
    /// bind a competing write arriving over a different protocol.
    /// </remarks>
    public static string? ResolveServiceName(
        MetadataV2Service? service,
        MetadataV2Publication? publication)
    {
        var name = service?.Metadata?.Name;
        return string.IsNullOrWhiteSpace(name) ? publication?.ServiceId : name;
    }

    /// <summary>
    /// Resolves the lease-namespace layer id for a published layer.
    /// </summary>
    /// <param name="publication">The publication backing the edited layer.</param>
    /// <param name="fallbackLayerId">The layer id to use when the publication carries no layer index.</param>
    /// <returns>The layer id a client would claim its lease under.</returns>
    public static int ResolveLayerId(MetadataV2Publication? publication, int fallbackLayerId)
        => publication?.LayerIndex ?? fallbackLayerId;

    /// <summary>
    /// Resolves the lock holder identity a request is editing under.
    /// </summary>
    /// <param name="context">The request being served, or <see langword="null"/>.</param>
    /// <returns>
    /// The caller's holder identity, or <see langword="null"/> when it cannot be
    /// determined. A <see langword="null"/> holder never satisfies a lock check, so
    /// an unidentifiable caller is blocked by any active lease rather than being
    /// waved through.
    /// </returns>
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

    /// <summary>
    /// Answers whether any lease could possibly be held, so a batch write can skip
    /// per-feature evaluation entirely on the uncontended path.
    /// </summary>
    /// <param name="locks">The lease store, or <see langword="null"/> when locks are not wired.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns><see langword="true"/> when per-feature evaluation is required.</returns>
    public static async ValueTask<bool> IsEvaluationRequiredAsync(
        IFeatureLockService? locks,
        CancellationToken cancellationToken = default)
        => locks is not null && await locks.HasAnyActiveLeasesAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Evaluates a single feature mutation against the active leases.
    /// </summary>
    /// <param name="guard">The edit guard, or <see langword="null"/> when locks are not wired.</param>
    /// <param name="serviceName">The service that owns the feature.</param>
    /// <param name="layerId">The protocol-facing layer id of the feature.</param>
    /// <param name="objectId">The feature's server-side OBJECTID.</param>
    /// <param name="operation">The edit operation name surfaced on the conflict.</param>
    /// <param name="holder">The caller's holder identity, from <see cref="ResolveHolder"/>.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>
    /// The typed conflict when the edit must be rejected; <see langword="null"/> when
    /// it may proceed.
    /// </returns>
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

    /// <summary>
    /// Evaluates a single feature mutation for a live request, resolving the lease
    /// store and guard from the request's service provider.
    /// </summary>
    /// <param name="context">The request being served.</param>
    /// <param name="serviceName">The service that owns the feature.</param>
    /// <param name="layerId">The protocol-facing layer id of the feature.</param>
    /// <param name="objectId">The feature's server-side OBJECTID.</param>
    /// <param name="operation">The edit operation name surfaced on the conflict.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>
    /// The typed conflict when the edit must be rejected; <see langword="null"/> when
    /// it may proceed, including when the host has no lease store wired at all.
    /// </returns>
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

    /// <summary>
    /// Renders a conflict as a short, client-facing message naming the blocking
    /// editor and when its lease expires.
    /// </summary>
    /// <param name="conflict">The conflict returned by <see cref="EvaluateAsync"/>.</param>
    /// <returns>A human-readable description of the conflict.</returns>
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

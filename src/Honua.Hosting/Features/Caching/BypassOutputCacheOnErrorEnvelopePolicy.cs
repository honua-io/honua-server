// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.OutputCaching;

namespace Honua.Infrastructure.Caching;

/// <summary>
/// Output cache policy that refuses storage for a response the shared error formatter wrote as
/// a GeoServices error envelope (<c>{"error":{"code":N,...}}</c>), whatever its HTTP status.
/// </summary>
/// <remarks>
/// <para>
/// GeoServices signals errors in the body and answers HTTP 200, so the framework's
/// status-code rule treats an envelope as a successful response and every named policy on a
/// GeoServices route would store it for the policy TTL. A transient provider failure (500, 501,
/// a retryable 503) was then replayed to every caller of the same key until it expired, and a
/// Redis-backed store kept it across a restart (honua-server#4980).
/// </para>
/// <para>
/// The formatter marks the response with <see cref="MarkErrorEnvelope"/> when it builds the
/// envelope, so the decision costs a dictionary lookup and never inspects the body. The policy
/// is part of the shared base policy, so it covers every route that opts into caching.
/// Deterministic refusals that a specific policy deliberately stores (an over-budget tile, see
/// <see cref="TileOutcomeOutputCachePolicy"/>) are re-admitted by that policy, which runs after
/// the base policy and reads the envelope code through <see cref="TryGetErrorEnvelopeCode"/>.
/// </para>
/// </remarks>
internal sealed class BypassOutputCacheOnErrorEnvelopePolicy : IOutputCachePolicy
{
    private static readonly object ErrorEnvelopeCodeKey = new();

    /// <summary>
    /// Records that the response for <paramref name="context"/> carries a GeoServices error
    /// envelope with the given body <paramref name="code"/>.
    /// </summary>
    internal static void MarkErrorEnvelope(HttpContext context, int code)
        => context.Items[ErrorEnvelopeCodeKey] = code;

    /// <summary>
    /// Returns the body code of the GeoServices error envelope written for
    /// <paramref name="context"/>, if the formatter wrote one.
    /// </summary>
    internal static bool TryGetErrorEnvelopeCode(HttpContext context, out int code)
    {
        if (context.Items.TryGetValue(ErrorEnvelopeCodeKey, out var value) && value is int envelopeCode)
        {
            code = envelopeCode;
            return true;
        }

        code = 0;
        return false;
    }

    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        if (TryGetErrorEnvelopeCode(context.HttpContext, out _))
        {
            context.AllowCacheStorage = false;
        }

        return ValueTask.CompletedTask;
    }
}

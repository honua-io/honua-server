// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Realtime;
using Honua.Infrastructure.Authentication;

namespace Honua.Server.Features.Realtime;

internal sealed partial class RealtimeDispatcher
{
    private static int InternalPriority(string family) => family switch
    {
        "security" => 0,
        "catalog" => 1,
        _ => 2
    };

    private bool ConsumesSecurity(string family) => Security is not null && InternalPriority(family) < 2;

    // Replay needs a refresh only while a security or catalog event is committed but not yet
    // applied by dispatch; dispatch applies each one before it advances past it.
    internal async Task RefreshReplaySecurityAsync(CancellationToken cancellationToken)
    {
        if (Security is not null && await HasPendingSecurityEventsAsync(cancellationToken).ConfigureAwait(false))
        {
            await Security.RefreshAsync(null, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> HasPendingSecurityEventsAsync(CancellationToken cancellationToken)
    {
        foreach (var family in (string[])[RealtimeLogFamilies.Security, RealtimeLogFamilies.Catalog])
        {
            var window = await log.ReadWindowAsync(family, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (!_initialized || window.Head > _dispatched.GetValueOrDefault(family))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Revocations close their streams directly. Every other change in the batch is covered by one
    // re-evaluation of every stream, or by one refresh per changed principal.
    private async Task RefreshSecurityBatchAsync(IReadOnlyList<RealtimeEvent> batch, CancellationToken cancellationToken)
    {
        var invalidations = batch.Select(ReadInvalidation).ToArray();
        foreach (var revoked in invalidations.OfType<LiveStreamInvalidation>().Where(IsRevocation).Distinct())
        {
            await Security!.RefreshAsync(revoked, cancellationToken).ConfigureAwait(false);
        }

        if (invalidations.Any(static invalidation => invalidation is null || invalidation.Kind == "policy-changed"))
        {
            await Security!.RefreshAsync(null, cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var principal in invalidations.OfType<LiveStreamInvalidation>().Where(static i => !IsRevocation(i)).Distinct())
        {
            await Security!.RefreshAsync(principal, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsRevocation(LiveStreamInvalidation invalidation) => invalidation.Kind == "credential-revoked";

    // Null means every stream is re-evaluated: a catalog change, or an event this build cannot read.
    private static LiveStreamInvalidation? ReadInvalidation(RealtimeEvent realtimeEvent)
    {
        if (realtimeEvent.Family.Name == RealtimeLogFamilies.Catalog)
        {
            return null;
        }

        var payload = realtimeEvent.Payload;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String
            || kind.GetString() is not ("policy-changed" or "principal-changed" or "credential-revoked")
            || !payload.TryGetProperty("subject", out var subject) || subject.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var credentialKind = payload.TryGetProperty("credentialKind", out var credential) && credential.ValueKind == JsonValueKind.String
            ? credential.GetString() : null;
        return new LiveStreamInvalidation(kind.GetString()!, subject.GetString()!, credentialKind);
    }
}

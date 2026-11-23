// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;

namespace Honua.Server.Features.Streaming;

internal static partial class FeatureStreamEndpoints
{
    // A subscription admitted after the stream opened is re-checked before it receives any event.
    private static async Task RevalidateAdmissionAsync(
        HttpContext context, LiveStreamPolicyRefreshFeature? livePolicy, CancellationToken cancellationToken)
    {
        if (livePolicy?.RevalidateAsync is { } revalidate)
        {
            await revalidate(static _ => Task.FromResult(true), cancellationToken).ConfigureAwait(false);
            context.RequestAborted.ThrowIfCancellationRequested();
        }
    }

    private sealed record EventDrivenBinding(HttpContext Context, StreamSubscriberSecurity Security);

    // Close the admission gap between the initial read and attaching the event consumer.
    private static async Task BindEventDrivenRefreshAsync(
        EventDrivenBinding binding, LiveStreamPolicyRefreshFeature livePolicy, CancellationToken cancellationToken)
    {
        binding.Security.EnableEventDrivenRefresh();
        if (livePolicy.RevalidateAsync is not { } revalidate)
        {
            return;
        }

        binding.Security.ReplayRevalidateAsync = token => RevalidateForReplayAsync(binding, revalidate, token);
        await binding.Security.RefreshForReplayAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RevalidateForReplayAsync(
        EventDrivenBinding binding,
        Func<Func<CancellationToken, Task<bool>>, CancellationToken, Task> revalidate,
        CancellationToken cancellationToken)
    {
        var authorized = false;
        await revalidate(async ct =>
        {
            authorized = await binding.Security.RefreshActivePolicyAsync(ct).ConfigureAwait(false);
            return authorized;
        }, cancellationToken).ConfigureAwait(false);
        binding.Context.RequestAborted.ThrowIfCancellationRequested();
        if (!authorized)
        {
            throw new UnauthorizedAccessException("Feature stream access was revoked.");
        }
    }
}

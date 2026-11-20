// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Security;

namespace Honua.Server.Features.Identity.Saml;

// The assertion is accepted only after the shared replay store records it; a store failure rejects it.
internal sealed class SamlAssertionReplayCache(ITokenReplayStore replays)
{
    private const string KeyPrefix = "saml:replay:";
    private const string RequestKeyPrefix = "saml:request:";
    private const string Marker = "saml";
    private const string FinalMarker = "saml:final";

    public bool TryRemember(string id, DateTimeOffset notOnOrAfter)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        return TryRegister(KeyPrefix + id.Trim(), LifetimeUntil(notOnOrAfter));
    }

    // An issued sign-in request ID may complete one sign-in; a second response for it is refused.
    public bool TryConsumeRequest(string requestId, TimeSpan lifetime)
        => !string.IsNullOrWhiteSpace(requestId) && TryRegister(RequestKeyPrefix + requestId.Trim(), lifetime);

    // Records the ID for its owner; true when it is new or already held by that same owner.
    public async Task<bool> TryClaimAsync(SamlReplayClaim claim, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(claim.Id))
        {
            return false;
        }

        try
        {
            var registration = new TokenReplayRegistrationRequest(
                KeyPrefix + claim.Id.Trim(), claim.OwnerMarker, LifetimeUntil(claim.NotOnOrAfter));
            var outcome = await replays.TryRegisterAsync(registration, cancellationToken).ConfigureAwait(false);
            return outcome.Registered || string.Equals(outcome.ExistingMarker, claim.OwnerMarker, StringComparison.Ordinal);
        }
        catch (Exception storeFailure) when (storeFailure is not OutOfMemoryException and not OperationCanceledException)
        {
            return false;
        }
    }

    // Moves a claimed ID from its owner to a final marker that no later claim can match.
    public async Task<bool> TryFinalizeAsync(SamlReplayClaim claim, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(claim.Id))
        {
            return false;
        }

        try
        {
            return await replays.TryRebindAsync(
                new TokenReplayRebinding(KeyPrefix + claim.Id.Trim(), claim.OwnerMarker, FinalMarker),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception storeFailure) when (storeFailure is not OutOfMemoryException and not OperationCanceledException)
        {
            return false;
        }
    }

    private static TimeSpan LifetimeUntil(DateTimeOffset notOnOrAfter)
        => notOnOrAfter > DateTimeOffset.UtcNow ? notOnOrAfter - DateTimeOffset.UtcNow : TimeSpan.FromMinutes(5);

    private bool TryRegister(string key, TimeSpan lifetime)
    {
        try
        {
            return replays.TryRegister(new TokenReplayRegistrationRequest(key, Marker, lifetime));
        }
        catch (Exception storeFailure) when (storeFailure is not OutOfMemoryException and not OperationCanceledException)
        {
            return false;
        }
    }
}

// An ID claimed by one owner (for example one browser session) until it is finalized.
internal readonly record struct SamlReplayClaim(string Id, string OwnerMarker, DateTimeOffset NotOnOrAfter);

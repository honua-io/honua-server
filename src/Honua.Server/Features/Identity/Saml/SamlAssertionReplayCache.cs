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

    public bool TryRemember(string id, DateTimeOffset notOnOrAfter)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var lifetime = notOnOrAfter > DateTimeOffset.UtcNow
            ? notOnOrAfter - DateTimeOffset.UtcNow
            : TimeSpan.FromMinutes(5);
        return TryRegister(KeyPrefix + id.Trim(), lifetime);
    }

    // An issued sign-in request ID may complete one sign-in; a second response for it is refused.
    public bool TryConsumeRequest(string requestId, TimeSpan lifetime)
        => !string.IsNullOrWhiteSpace(requestId) && TryRegister(RequestKeyPrefix + requestId.Trim(), lifetime);

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

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Security;

namespace Honua.Server.Features.Identity.Saml;

internal interface ISamlAssertionReplayCache
{
    // A repeated, empty, or unrecordable assertion id is rejected.
    bool TryRemember(string id, DateTimeOffset notOnOrAfter);
}

// The assertion is accepted only after the shared replay store records it; a store failure rejects it.
internal sealed class SamlAssertionReplayCache(ITokenReplayStore replays) : ISamlAssertionReplayCache
{
    private const string KeyPrefix = "saml:replay:";
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
        try
        {
            return replays.TryRegister(new TokenReplayRegistrationRequest(KeyPrefix + id.Trim(), Marker, lifetime));
        }
        catch (Exception storeFailure) when (storeFailure is not OutOfMemoryException and not OperationCanceledException)
        {
            return false;
        }
    }
}

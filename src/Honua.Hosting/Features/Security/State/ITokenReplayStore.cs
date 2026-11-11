// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Security;

// ExistingMarker is null when this call registered the key.
internal readonly record struct TokenReplayRegistrationOutcome(bool Registered, string? ExistingMarker);

internal readonly record struct TokenReplayRegistrationRequest(string ReplayKey, string Marker, TimeSpan Lifetime);

internal readonly record struct TokenReplayRebinding(string ReplayKey, string FromMarker, string ToMarker);

internal interface ITokenReplayStore
{
    Task<TokenReplayRegistrationOutcome> TryRegisterAsync(TokenReplayRegistrationRequest request, CancellationToken cancellationToken);

    // True when the key now carries ToMarker: it carried FromMarker, or already carried ToMarker.
    Task<bool> TryRebindAsync(TokenReplayRebinding rebinding, CancellationToken cancellationToken);

    // For callers on a synchronous validation path.
    bool TryRegister(TokenReplayRegistrationRequest request);
}

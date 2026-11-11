// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Security;

// For a host with no system database, which is one process by definition, and for tests.
internal sealed class InMemoryTokenReplayStore(TimeProvider? timeProvider = null) : ITokenReplayStore
{
    private readonly Dictionary<string, Registration> _registrations = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<TokenReplayRegistrationOutcome> TryRegisterAsync(
        TokenReplayRegistrationRequest request,
        CancellationToken cancellationToken)
        => Task.FromResult(Register(request));

    public Task<bool> TryRebindAsync(TokenReplayRebinding rebinding, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!TryGetLive(rebinding.ReplayKey, out var current)
                || (current.Marker != rebinding.FromMarker && current.Marker != rebinding.ToMarker))
            {
                return Task.FromResult(false);
            }

            _registrations[rebinding.ReplayKey] = current with { Marker = rebinding.ToMarker };
            return Task.FromResult(true);
        }
    }

    public bool TryRegister(TokenReplayRegistrationRequest request) => Register(request).Registered;

    private TokenReplayRegistrationOutcome Register(TokenReplayRegistrationRequest request)
    {
        lock (_gate)
        {
            if (TryGetLive(request.ReplayKey, out var existing))
            {
                return new TokenReplayRegistrationOutcome(Registered: false, existing.Marker);
            }

            _registrations[request.ReplayKey] = new Registration(request.Marker, _timeProvider.GetUtcNow().Add(request.Lifetime));
            return new TokenReplayRegistrationOutcome(Registered: true, ExistingMarker: null);
        }
    }

    private bool TryGetLive(string replayKey, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Registration? registration)
    {
        if (_registrations.TryGetValue(replayKey, out registration) && registration.ExpiresAt > _timeProvider.GetUtcNow())
        {
            return true;
        }

        _registrations.Remove(replayKey);
        registration = null;
        return false;
    }

    private sealed record Registration(string Marker, DateTimeOffset ExpiresAt);
}

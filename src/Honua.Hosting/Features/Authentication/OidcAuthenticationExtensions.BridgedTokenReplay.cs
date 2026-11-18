// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Security;
using Microsoft.IdentityModel.Tokens;

namespace Honua.Infrastructure.Authentication;

// A token exchanged for a server credential is recorded as an ordinary HTTP API admission, so
// repeat exchanges stay allowed while a token bound to another surface cannot be exchanged.
public static partial class OidcAuthenticationExtensions
{
    internal static async Task<bool> TryAdmitExchangedTokenAsync(
        SecurityToken token,
        ExchangedTokenReplayContext replay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        var options = replay.Options;
        if (!options.EnableTokenReplayProtection)
        {
            return true;
        }

        // A token without an expiry cannot be presented as a bearer, so there is no other surface to
        // protect; one that outlives the bearer retention limit is recorded for the bounded window.
        var tokenKey = TryGetTokenReplayKey(token);
        var expiration = ResolveExchangeReplayExpiration(token, options, DateTime.UtcNow);
        if (string.IsNullOrWhiteSpace(tokenKey) || expiration is not { } expiresOn)
        {
            return true;
        }

        var registration = await TryRegisterTokenReplayAsync(
            new TokenReplayPresentation(tokenKey, expiresOn, TokenReplayScope.HttpApi),
            replay.Replays,
            options.ReplayProtectionFailClosed,
            replay.Logger,
            cancellationToken).ConfigureAwait(false);
        return registration.Result switch
        {
            TokenReplayRegistrationResult.Registered or TokenReplayRegistrationResult.Reused
                or TokenReplayRegistrationResult.Skipped => true,
            _ => false,
        };
    }

    private static DateTime? ResolveExchangeReplayExpiration(SecurityToken token, TokenValidationOptions options, DateTime now)
    {
        if (GetReplayCacheExpiration(token, options, now) is { } expiration)
        {
            return expiration;
        }

        var maximumRetention = options.TokenReplayCacheDuration > TimeSpan.Zero
            ? options.TokenReplayCacheDuration : TimeSpan.FromHours(24);
        return token.ValidTo != DateTime.MinValue && token.ValidTo.ToUniversalTime() > now
            ? now.Add(maximumRetention)
            : null;
    }
}

internal readonly record struct ExchangedTokenReplayContext(
    TokenValidationOptions Options,
    ITokenReplayStore? Replays,
    ILogger Logger);

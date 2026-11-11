// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Infrastructure.Security;
using Microsoft.IdentityModel.Tokens;

namespace Honua.Infrastructure.Authentication;

// An ordinary API token stays reusable, a continuation surface registers it as single-use until the server-minted continuation binds it, and reuse across surfaces is a replay.
public static partial class OidcAuthenticationExtensions
{
    private const string RegisteredTokenReplayValue = "1";
    private const string HttpApiTokenReplayValue = "surface:http";
    private const string TokenReplayContinuationPrefix = "continuation:";

    // Moves a first-use registration to a continuation, keeping its expiry. Binding the same
    // continuation again is idempotent; a token already bound to a different continuation, or
    // admitted on the HTTP API, is never moved, so a replayed request cannot steal another
    // session's binding.
    internal static async Task<bool> TryBindTokenReplayContinuationAsync(HttpContext context, string continuationId)
    {
        ArgumentNullException.ThrowIfNull(context);
        var admission = context.Features.Get<TokenReplayAdmissionFeature>();
        var replays = context.RequestServices.GetService<ITokenReplayStore>();
        if (admission is null || replays is null || string.IsNullOrWhiteSpace(continuationId) || !admission.Scope.CanBindContinuation)
        {
            return false;
        }

        try
        {
            return await replays.TryRebindAsync(
                new TokenReplayRebinding(admission.TokenKey, RegisteredTokenReplayValue, BuildTokenReplayContinuationMarker(continuationId)),
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception storeFailure) when (storeFailure is not OutOfMemoryException and not OperationCanceledException)
        {
            OidcAuthenticationLog.TokenReplayContinuationBindFailed(
                context.RequestServices.GetRequiredService<ILogger<OidcAuthenticationOptions>>(), storeFailure);
            return false;
        }
    }

    // The replay store keeps only the continuation digest because the continuation id names a live session.
    internal static string BuildTokenReplayContinuationMarker(string continuationId) =>
        TokenReplayContinuationPrefix
        + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(continuationId)));

    private static TokenReplayScope ResolveTokenReplayScope(HttpContext context, SecurityToken token)
    {
        foreach (var resolver in context.RequestServices.GetServices<ITokenReplayScopeResolver>())
        {
            if (!resolver.OwnsRequest(context))
            {
                continue;
            }

            return resolver.ResolveScope(context, EncodedTokenFingerprint(token));
        }

        return TokenReplayScope.HttpApi;
    }

    private static TokenReplayRegistrationResult ClassifyReusedToken(string? value, TokenReplayScope scope)
    {
        if (scope.ContinuationMarker is not null
            && string.Equals(value, scope.ContinuationMarker, StringComparison.Ordinal))
        {
            return TokenReplayRegistrationResult.Continued;
        }

        return !scope.CanBindContinuation && string.Equals(value, scope.RegistrationValue, StringComparison.Ordinal)
            ? TokenReplayRegistrationResult.Reused
            : TokenReplayRegistrationResult.ReplayDetected;
    }

    internal readonly record struct TokenReplayRegistration(TokenReplayRegistrationResult Result, bool Recorded);

    internal readonly record struct TokenReplayPresentation(string TokenKey, DateTime ExpiresOn, TokenReplayScope Scope);

    internal readonly record struct TokenReplayScope(string RegistrationValue, string? ContinuationMarker)
    {
        public static TokenReplayScope HttpApi => new(HttpApiTokenReplayValue, null);

        public static TokenReplayScope ContinuationSurface(string? continuationMarker) =>
            new(RegisteredTokenReplayValue, continuationMarker);

        public static TokenReplayScope ReusableSurface(string surface, string validatedTokenFingerprint) =>
            new($"surface:{surface}:{validatedTokenFingerprint}", null);

        public bool CanBindContinuation => RegistrationValue == RegisteredTokenReplayValue;
    }
}

// An implementation may claim only its own routes, because admission there makes the token a replay on the ordinary API and an API admission is a replay on that surface.
internal interface ITokenReplayScopeResolver
{
    bool OwnsRequest(HttpContext context);

    OidcAuthenticationExtensions.TokenReplayScope ResolveScope(HttpContext context, string? validatedTokenFingerprint);
}

// The continuation binding is created by the server and a client cannot supply it.
internal sealed class TokenReplayAdmissionFeature(
    string tokenKey,
    OidcAuthenticationExtensions.TokenReplayScope? scope = null)
{
    public string TokenKey { get; } = tokenKey;
    public OidcAuthenticationExtensions.TokenReplayScope Scope { get; } =
        scope ?? OidcAuthenticationExtensions.TokenReplayScope.ContinuationSurface(null);
}

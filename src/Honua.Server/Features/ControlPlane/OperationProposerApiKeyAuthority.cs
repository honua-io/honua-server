// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Infrastructure.Authentication;

namespace Honua.ControlPlane;

/// <summary>Current stored API-key authority; approval never revives a revoked proposer.</summary>
internal sealed class OperationProposerApiKeyAuthority(TimeProvider timeProvider, IAdminApiKeyStore? apiKeys = null)
{
    internal const string Refusal = "The proposer API-key authority is unavailable or no longer authorized.";
    private const string Scheme = AuthenticationExtensions.ApiKeyScheme;
    private const string Prefix = Scheme + ":api-key:";

    internal static bool IsApiKeyActor(string? actor) => actor is not null
        && (string.Equals(actor, Scheme, StringComparison.OrdinalIgnoreCase)
            || actor.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase));

    internal async Task ValidateAsync(string? actor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (actor is null || actor.Length != Prefix.Length + 36
            || !actor.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(actor[Prefix.Length..], "D", out var id) || id == Guid.Empty || apiKeys is null)
            throw Denied();
        AdminApiKeyRecord? record;
        try { record = await apiKeys.GetAsync(id, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException) { throw Denied(); }
        cancellationToken.ThrowIfCancellationRequested();
        var now = timeProvider.GetUtcNow();
        if (record is null || record.Id != id || record.RevokedAt is not null
            || (record.ExpiresAt is { } expiry && expiry <= now)) throw Denied();
        var identity = new ClaimsIdentity(record.Permissions.Select(permission =>
            new Claim(AdminApiKeyPermission.PermissionClaimType, permission)), "proposal-key-revalidation");
        identity.AddClaim(new Claim(ClaimTypes.Role, "admin"));
        if (!AdminApiKeyPermission.IsAuthorized(new ClaimsPrincipal(identity), HttpMethods.Post)) throw Denied();
    }

    private static InvalidOperationException Denied() => new(Refusal);
}

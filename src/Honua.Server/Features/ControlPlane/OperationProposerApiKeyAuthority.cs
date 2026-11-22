// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Infrastructure.Authentication;

namespace Honua.ControlPlane;

// Approval never revives a revoked proposer key.
internal sealed class OperationProposerApiKeyAuthority(TimeProvider timeProvider, IAdminApiKeyStore? apiKeys = null)
{
    internal const string Refusal = "The proposer API-key authority is unavailable or no longer authorized.";
    internal const string BootstrapRefusal =
        "The bootstrap admin credential cannot file a proposal: approval re-checks the proposer's API key, "
        + "and this credential has none, so the proposal could never be approved. Create a scoped API key with "
        + "POST /api/v1/admin/api-keys (for an MCP client, grant agent and admin:write) and send it as X-API-Key.";
    private const string Scheme = AuthenticationExtensions.ApiKeyScheme;
    private const string Prefix = Scheme + ":api-key:";
    private const string SupportAgentKeyPrefix = Scheme + ":support-agent:";

    internal static bool IsApiKeyActor(string? actor) => actor is not null
        && (string.Equals(actor, Scheme, StringComparison.OrdinalIgnoreCase)
            || actor.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase));

    // The bootstrap password has no persisted key record, so approval would always refuse its proposal.
    internal static bool IsBootstrapCredentialActor(string? actor) => IsApiKeyActor(actor)
        && (actor!.EndsWith(":api-key:" + Guid.Empty.ToString("D"), StringComparison.OrdinalIgnoreCase)
            || (TryGetKeyId(actor, out var id) && id == ApiKeyAuthenticationHandler.BootstrapAdminActorId));

    internal static bool IsSupportOrOAuthActor(string? actor) => actor is not null
        && (actor.Contains(":support-agent:", StringComparison.OrdinalIgnoreCase)
            || actor.Contains(":oauth:", StringComparison.OrdinalIgnoreCase));

    internal static bool TryGetKeyId(string? actor, out Guid id)
    {
        id = Guid.Empty;
        if (actor is null)
        {
            return false;
        }

        if (actor.Length == Prefix.Length + 36 &&
            actor.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(actor[Prefix.Length..], "D", out id) && id != Guid.Empty)
        {
            return true;
        }

        if (actor.Length == SupportAgentKeyPrefix.Length + 36 &&
            actor.StartsWith(SupportAgentKeyPrefix, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(actor[SupportAgentKeyPrefix.Length..], "D", out id) && id != Guid.Empty)
        {
            return true;
        }

        const string genericMarker = ":api-key:";
        var markerIndex = actor.IndexOf(genericMarker, StringComparison.OrdinalIgnoreCase);
        return markerIndex > 0 &&
            actor.Length == markerIndex + genericMarker.Length + 36 &&
            Guid.TryParseExact(actor[(markerIndex + genericMarker.Length)..], "D", out id) && id != Guid.Empty;
    }

    internal async Task ValidateAsync(string? actor, CancellationToken cancellationToken)
    {
        if (IsSupportOrOAuthActor(actor) && !IsApiKeyActor(actor))
        {
            return;
        }

        await GetCurrentRecordAsync(actor, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<AdminApiKeyRecord> GetCurrentRecordAsync(string? actor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetKeyId(actor, out var id) || apiKeys is null)
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
        var principal = new ClaimsPrincipal(identity);
        if (!AdminApiKeyPermission.IsAuthorized(principal, HttpMethods.Post)
            && !AdminApiKeyPermission.HasOpsProposeGrant(principal)) throw Denied();
        return record;
    }

    private static InvalidOperationException Denied() => new(Refusal);
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Honua.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Identity.Saml;

// The IdP names the sign-in it is ending by NameID and SessionIndex, and its logout request arrives
// cross-site, without the session cookie. This index maps that pair to the sessions it started, in the
// shared store, so a logout request can end them. Session ids are kept protected with the key ring.
internal sealed class SamlSessionIndex(
    ISecurityTokenStore tokens,
    IDataProtectionProvider protection,
    IOptions<SamlAuthenticationOptions> options)
{
    private const char Separator = '\n';
    private readonly IDataProtector _protector = protection.CreateProtector("Honua.Saml.SessionIndex.v1");
    private readonly SamlAuthenticationOptions _options = options.Value;

    // Adds the session to those started by this IdP sign-in and keeps the entry until the longest of
    // them expires. A store failure throws, so a session that a logout request could not reach is not
    // issued. The store has no compare-and-swap: two sign-ins under the same IdP session at the same
    // instant can drop one id (accepted; that session's cookie was replaced in the same sign-in race).
    public async Task RecordAsync(SamlIndexedSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var key = Key(session.Reference);
        var stored = await tokens.FindAsync(key, cancellationToken).ConfigureAwait(false);
        var sessionIds = stored is null ? [] : Unprotect(stored.Payload);
        sessionIds.Add(session.SessionId);
        var expiresAt = stored is not null && stored.ExpiresAt > session.ExpiresAt
            ? stored.ExpiresAt
            : session.ExpiresAt;
        var payload = _protector.Protect(Encoding.UTF8.GetBytes(string.Join(Separator, sessionIds)));
        await tokens.StoreAsync(key, payload, expiresAt - DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
    }

    // The session ids this IdP sign-in started (empty when none are known).
    public async Task<IReadOnlyList<string>> FindAsync(SamlSessionReference reference, CancellationToken cancellationToken)
    {
        var stored = await tokens.FindAsync(Key(reference), cancellationToken).ConfigureAwait(false);
        return stored is null ? [] : Unprotect(stored.Payload);
    }

    // Called once every session it names has ended, so a failed termination can be retried.
    public Task ForgetAsync(SamlSessionReference reference, CancellationToken cancellationToken)
        => tokens.RevokeAsync(Key(reference), cancellationToken);

    private List<string> Unprotect(byte[] payload)
    {
        try
        {
            var joined = Encoding.UTF8.GetString(_protector.Unprotect(payload));
            return [.. joined.Split(Separator, StringSplitOptions.RemoveEmptyEntries)];
        }
        // A payload protected with a retired key cannot name a live session any more: session records
        // are protected with the same key ring.
        catch (CryptographicException)
        {
            return [];
        }
    }

    // Each part is length-prefixed, so no NameID or SessionIndex value can shift the boundary between them.
    private SecurityTokenKey Key(SamlSessionReference reference)
        => new(
            SecurityTokenKind.SamlSessionIndex,
            string.Concat(Part(_options.IdpEntityId), Part(reference.NameId), Part(reference.SessionIndex)));

    private static string Part(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        return trimmed.Length.ToString(CultureInfo.InvariantCulture) + ":" + trimmed;
    }
}

// The IdP's name for one of its sign-ins: the subject's NameID and the SessionIndex it assigned.
internal readonly record struct SamlSessionReference(string NameId, string SessionIndex);

// A session started by an IdP sign-in, filed under that sign-in's reference.
internal sealed record SamlIndexedSession(SamlSessionReference Reference, string SessionId, DateTimeOffset ExpiresAt);

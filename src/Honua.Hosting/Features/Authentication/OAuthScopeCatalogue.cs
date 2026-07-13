// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// OAuth2 scope catalogue (ADR-0053 Increment 2, #1888). Increment 1 only let a
/// requested <c>scope</c> <em>narrow</em> to roles the credential already held.
/// This catalogue defines named scopes and maps each to the set of RBAC
/// permissions it grants, so a token minted for a client carries exactly the
/// permissions of the granted scopes — bounded by the scopes the client is
/// allowed to request and by the per-request <c>scope</c> narrowing.
/// </summary>
/// <remarks>
/// In-memory to mirror the established auth store pattern (ADR-0049 — no parallel
/// durable store). Scope narrowing/escalation is enforced here: a permission is
/// only ever granted through a defined scope the client is allowed to request.
/// </remarks>
internal interface IOAuthScopeCatalogue
{
    /// <summary>Lists every defined scope.</summary>
    Task<IReadOnlyList<OAuthScopeDefinition>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Fetches a scope by its canonical name, or null.</summary>
    Task<OAuthScopeDefinition?> GetAsync(string scope, CancellationToken cancellationToken);

    /// <summary>Defines (or replaces) a scope and its permission mapping.</summary>
    Task<OAuthScopeDefinition> DefineAsync(OAuthScopeDefinition definition, CancellationToken cancellationToken);

    /// <summary>Removes a scope from the catalogue. Returns the removed definition, or null.</summary>
    Task<OAuthScopeDefinition?> DeleteAsync(string scope, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a space-delimited requested-scope string into the flattened set of
    /// granted RBAC permissions. A requested scope is only honoured when (1) it is
    /// defined in the catalogue and (2) it is in <paramref name="allowedScopes"/>
    /// (the client's registered scopes). Unknown or disallowed scopes are dropped,
    /// never escalated. Returns the granted permissions and the scopes actually
    /// granted (for the token response).
    /// </summary>
    Task<OAuthScopeGrant> ResolveGrantAsync(
        string? requestedScope,
        IReadOnlyList<string> allowedScopes,
        CancellationToken cancellationToken);
}

/// <summary>A named OAuth2 scope and the RBAC permissions it grants.</summary>
internal sealed record OAuthScopeDefinition(
    string Scope,
    string Description,
    IReadOnlyList<string> Permissions);

/// <summary>The outcome of resolving a requested scope against the catalogue.</summary>
internal sealed record OAuthScopeGrant(
    IReadOnlyList<string> GrantedScopes,
    IReadOnlyList<string> GrantedPermissions);

internal sealed class InMemoryOAuthScopeCatalogue : IOAuthScopeCatalogue
{
    private readonly ConcurrentDictionary<string, OAuthScopeDefinition> _scopes =
        new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<OAuthScopeDefinition>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<OAuthScopeDefinition> result = _scopes.Values
            .OrderBy(static scope => scope.Scope, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
        return Task.FromResult(result);
    }

    public Task<OAuthScopeDefinition?> GetAsync(string scope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(scope))
        {
            return Task.FromResult<OAuthScopeDefinition?>(null);
        }

        _scopes.TryGetValue(scope.Trim(), out var definition);
        return Task.FromResult(definition);
    }

    public Task<OAuthScopeDefinition> DefineAsync(OAuthScopeDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = definition with
        {
            Scope = definition.Scope.Trim(),
            Description = definition.Description?.Trim() ?? string.Empty,
            Permissions = NormalizePermissions(definition.Permissions),
        };

        _scopes[normalized.Scope] = normalized;
        return Task.FromResult(normalized);
    }

    public Task<OAuthScopeDefinition?> DeleteAsync(string scope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(scope))
        {
            return Task.FromResult<OAuthScopeDefinition?>(null);
        }

        _scopes.TryRemove(scope.Trim(), out var removed);
        return Task.FromResult(removed);
    }

    public Task<OAuthScopeGrant> ResolveGrantAsync(
        string? requestedScope,
        IReadOnlyList<string> allowedScopes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var allowed = new HashSet<string>(
            allowedScopes ?? [],
            StringComparer.OrdinalIgnoreCase);

        // A request that omits scope is granted the client's full allowed-scope set
        // (RFC 6749 §3.3: the authorization server MAY use a default scope). An empty
        // allowed-scope set therefore grants nothing — never an implicit escalation.
        IEnumerable<string> candidates = string.IsNullOrWhiteSpace(requestedScope)
            ? allowed
            : requestedScope
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(allowed.Contains);

        var grantedScopes = new List<string>();
        var grantedPermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (!_scopes.TryGetValue(candidate, out var definition))
            {
                // A scope the client may request but that is not defined in the
                // catalogue grants no permissions (cannot escalate to undefined).
                continue;
            }

            grantedScopes.Add(definition.Scope);
            foreach (var permission in definition.Permissions)
            {
                grantedPermissions.Add(permission);
            }
        }

        var grant = new OAuthScopeGrant(
            grantedScopes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            grantedPermissions.ToArray());
        return Task.FromResult(grant);
    }

    private static string[] NormalizePermissions(IReadOnlyList<string>? permissions)
    {
        if (permissions is null)
        {
            return [];
        }

        return permissions
            .Select(static permission => permission?.Trim() ?? string.Empty)
            .Where(static permission => permission.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

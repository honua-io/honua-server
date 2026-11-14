// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;

namespace Honua.Infrastructure.Authentication;

internal interface IOAuthScopeCatalogue
{
    Task<IReadOnlyList<OAuthScopeDefinition>> ListAsync(CancellationToken cancellationToken);

    Task<OAuthScopeDefinition?> GetAsync(string scope, CancellationToken cancellationToken);

    Task<OAuthScopeDefinition> DefineAsync(OAuthScopeDefinition definition, CancellationToken cancellationToken);

    Task<OAuthScopeDefinition?> DeleteAsync(string scope, CancellationToken cancellationToken);

    // A requested scope is kept only when it is in the catalogue and in the client's registered scopes; anything else is dropped and never escalated.
    Task<OAuthScopeGrant> ResolveGrantAsync(
        string? requestedScope,
        IReadOnlyList<string> allowedScopes,
        CancellationToken cancellationToken);
}

internal sealed record OAuthScopeDefinition(
    string Scope,
    string Description,
    IReadOnlyList<string> Permissions);

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

        var normalized = OAuthScopePolicy.Normalize(definition);

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

        return Task.FromResult(OAuthScopePolicy.Resolve(requestedScope, allowedScopes, _scopes));
    }
}

internal static class OAuthScopePolicy
{
    public static OAuthScopeDefinition Normalize(OAuthScopeDefinition definition)
    {
        return definition with
        {
            Scope = definition.Scope.Trim(),
            Description = definition.Description?.Trim() ?? string.Empty,
            Permissions = NormalizePermissions(definition.Permissions),
        };
    }

    public static string[] Candidates(string? requestedScope, IReadOnlyList<string> allowedScopes)
    {
        var allowed = new HashSet<string>(allowedScopes ?? [], StringComparer.OrdinalIgnoreCase);
        return (string.IsNullOrWhiteSpace(requestedScope)
            ? allowed
            : requestedScope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(allowed.Contains)).ToArray();
    }

    public static OAuthScopeGrant Resolve(string? requestedScope, IReadOnlyList<string> allowedScopes,
        IReadOnlyDictionary<string, OAuthScopeDefinition> definitions)
    {
        var grantedScopes = new List<string>();
        var grantedPermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in Candidates(requestedScope, allowedScopes))
        {
            if (!definitions.TryGetValue(candidate, out var definition))
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
        return grant;
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

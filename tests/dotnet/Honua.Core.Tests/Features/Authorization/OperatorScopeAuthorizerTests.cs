// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Tests.Features.Authorization;

/// <summary>
/// Unit tests for <see cref="OperatorScopeAuthorizer"/> (honua-server#2851): the OAuth 2.1
/// scope narrowing that intersects a bearer token's scopes with the operator grant model.
/// The suite proves the invariant the acceptance criteria hinge on — scope narrowing can only
/// ever <em>narrow</em> authority, never widen it, and a scope-governed token with no
/// recognized scope is fail-closed — independently of whether the grant check would allow.
/// </summary>
public sealed class OperatorScopeAuthorizerTests
{
    private readonly OperatorScopeAuthorizer _authorizer = new();

    [Fact]
    public void Evaluate_NonOAuthPrincipal_IsNotScopeGovernedAndNotNarrowed()
    {
        // An X-API-Key / interactive principal carries neither the bearer marker nor a scope
        // claim, so scope narrowing does not apply and the grant decision stands unchanged.
        var principal = ApiKeyPrincipal();

        var decision = _authorizer.Evaluate(principal, OperatorResourceType.Process, OperatorOperation.ExecuteCustomCode);

        decision.IsScopeGoverned.Should().BeFalse();
        decision.IsAllowed.Should().BeTrue("non-OAuth principals are never narrowed by scopes");
    }

    [Fact]
    public void Evaluate_BearerWithMatchingScope_IsAllowed()
    {
        var principal = BearerPrincipal(OperatorScopeCatalog.Execute);

        var decision = _authorizer.Evaluate(principal, OperatorResourceType.Process, OperatorOperation.Execute);

        decision.IsScopeGoverned.Should().BeTrue();
        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_BearerWithNarrowerScope_CannotEscalateToStrongerOperation()
    {
        // A discover-only token cannot reach Execute — the central "narrowing cannot escalate"
        // invariant. This holds even though a grant check upstream might allow Execute.
        var principal = BearerPrincipal(OperatorScopeCatalog.Discover);

        var discover = _authorizer.Evaluate(principal, OperatorResourceType.Catalog, OperatorOperation.Discover);
        var execute = _authorizer.Evaluate(principal, OperatorResourceType.Process, OperatorOperation.Execute);
        var mutating = _authorizer.Evaluate(principal, OperatorResourceType.Process, OperatorOperation.ExecuteMutatingProcess);

        discover.IsAllowed.Should().BeTrue("the token holds honua.mcp.discover");
        execute.IsAllowed.Should().BeFalse("honua.mcp.discover does not include Execute");
        execute.Reason.Should().NotBeNullOrWhiteSpace();
        mutating.IsAllowed.Should().BeFalse("honua.mcp.discover does not include ExecuteMutatingProcess");
    }

    [Fact]
    public void Evaluate_BearerWithNoRecognizedScope_IsFailClosed()
    {
        // A bearer token that presents no recognized Honua MCP scope authorizes nothing — the
        // documented fail-closed default — regardless of resource/operation.
        var noScope = BearerPrincipal();
        var unknownScope = BearerPrincipal("some.other.api.read", "openid", "profile");

        foreach (var operation in Enum.GetValues<OperatorOperation>())
        {
            _authorizer.Evaluate(noScope, OperatorResourceType.Catalog, operation).IsAllowed
                .Should().BeFalse($"a token with no scope is fail-closed for {operation}");
            _authorizer.Evaluate(unknownScope, OperatorResourceType.Catalog, operation).IsAllowed
                .Should().BeFalse($"unrecognized scopes grant nothing for {operation}");
        }
    }

    [Fact]
    public void Evaluate_BearerWithFullScope_PermitsEveryOperation()
    {
        // honua.mcp.full opts a token out of narrowing: it is bounded only by grants. This is
        // the one scope that restores the pre-scope "token authority == grant authority".
        var principal = BearerPrincipal(OperatorScopeCatalog.Full);

        foreach (var operation in Enum.GetValues<OperatorOperation>())
        {
            _authorizer.Evaluate(principal, OperatorResourceType.Process, operation).IsAllowed
                .Should().BeTrue($"honua.mcp.full permits {operation}");
        }
    }

    [Fact]
    public void Evaluate_ReadScope_ImpliesDiscover()
    {
        var principal = BearerPrincipal(OperatorScopeCatalog.Read);

        _authorizer.Evaluate(principal, OperatorResourceType.Job, OperatorOperation.Read).IsAllowed.Should().BeTrue();
        _authorizer.Evaluate(principal, OperatorResourceType.Catalog, OperatorOperation.Discover).IsAllowed.Should().BeTrue();
        _authorizer.Evaluate(principal, OperatorResourceType.Process, OperatorOperation.Execute).IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_MutatingScope_ImpliesBaselineExecute()
    {
        var principal = BearerPrincipal(OperatorScopeCatalog.ExecuteMutating);

        _authorizer.Evaluate(principal, OperatorResourceType.Process, OperatorOperation.ExecuteMutatingProcess).IsAllowed.Should().BeTrue();
        _authorizer.Evaluate(principal, OperatorResourceType.Process, OperatorOperation.Execute).IsAllowed.Should().BeTrue();
        _authorizer.Evaluate(principal, OperatorResourceType.Process, OperatorOperation.ExecuteCustomCode).IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_SpaceDelimitedScopeClaim_IsExpanded()
    {
        // OAuth scope is a single space-delimited claim value (RFC 9068), not multiple claims.
        var principal = BearerPrincipal($"{OperatorScopeCatalog.Discover} {OperatorScopeCatalog.Create}");

        _authorizer.Evaluate(principal, OperatorResourceType.Catalog, OperatorOperation.Discover).IsAllowed.Should().BeTrue();
        _authorizer.Evaluate(principal, OperatorResourceType.Workspace, OperatorOperation.Create).IsAllowed.Should().BeTrue();
        _authorizer.Evaluate(principal, OperatorResourceType.Process, OperatorOperation.Execute).IsAllowed.Should().BeFalse();
    }

    private static ClaimsPrincipal BearerPrincipal(params string[] scopes)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "operator-123"),
            // The marker the JwtBearer OnTokenValidated hook stamps on every validated token.
            new(OperatorScopeCatalog.ScopeGovernedClaimType, OperatorScopeCatalog.ScopeGovernedClaimValue),
        };

        if (scopes.Length > 0)
        {
            claims.Add(new Claim(OperatorScopeCatalog.ScopeClaimType, string.Join(' ', scopes)));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Bearer"));
    }

    private static ClaimsPrincipal ApiKeyPrincipal()
        => new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "api-key-user"),
                new Claim(ClaimTypes.Role, "admin"),
                new Claim("auth_type", "admin"),
            ],
            authenticationType: "ApiKey"));
}
